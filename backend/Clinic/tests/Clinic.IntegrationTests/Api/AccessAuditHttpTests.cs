using System.Data.Common;
using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Clinic.Domain.Entities;
using Clinic.Domain.Enums;
using Clinic.Infrastructure.Authentication;
using Clinic.Infrastructure.Persistence;
using Clinic.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Clinic.IntegrationTests.Api;

public sealed class AccessAuditHttpTests(SqlDatabaseFixture database) : IClassFixture<SqlDatabaseFixture>
{
    internal sealed class FailAuditInsert : DbCommandInterceptor
    {
        public bool Enabled { get; set; }
        public int Attempts { get; private set; }
        private void Check(DbCommand command)
        {
            if (!command.CommandText.Contains("INSERT INTO [AuditEvents]", StringComparison.OrdinalIgnoreCase)) return;
            Attempts++;
            if (Enabled) throw new InvalidOperationException("Synthetic audit outage: must not reach response.");
        }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        { Check(command); return ValueTask.FromResult(result); }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        { Check(command); return ValueTask.FromResult(result); }
    }

    private sealed class Harness(WebApplicationFactory<Program> factory, FailAuditInsert failure,
        Patient patient, Doctor doctor, Visit visit, Prescription prescription, Appointment appointment) : IDisposable
    {
        public WebApplicationFactory<Program> Factory { get; } = factory;
        public FailAuditInsert Failure { get; } = failure;
        public Patient Patient { get; } = patient;
        public Doctor Doctor { get; } = doctor;
        public Visit Visit { get; } = visit;
        public Prescription Prescription { get; } = prescription;
        public Appointment Appointment { get; } = appointment;
        public void Dispose() => Factory.Dispose();
        public string Path(string kind) => kind switch
        {
            "patient" => $"/api/staff/patients/{Patient.Id}",
            "profile" => $"/api/staff/patients/{Patient.Id}/medical-profile",
            "visit" => $"/api/staff/patients/{Patient.Id}/visits/{Visit.Id}",
            "visits" => $"/api/staff/patients/{Patient.Id}/visits",
            "prescription" => $"/api/staff/patients/{Patient.Id}/prescriptions/{Prescription.Id}",
            "prescriptions" => $"/api/staff/patients/{Patient.Id}/prescriptions",
            "visit-prescriptions" => $"/api/staff/patients/{Patient.Id}/visits/{Visit.Id}/prescriptions",
            "pdf" => $"/api/staff/patients/{Patient.Id}/prescriptions/{Prescription.Id}/pdf?language=en",
            "appointment" => $"/api/staff/doctors/{Doctor.Id}/appointments/{Appointment.Id}/rescheduling",
            _ => throw new ArgumentException(nameof(kind))
        };
    }

    private async Task<Harness> Seed()
    {
        var now = DateTimeOffset.UtcNow;
        var patient = new Patient("SensitiveName", "SensitivePhone");
        var doctor = new Doctor("Synthetic doctor");
        var visit = new Visit(patient.Id, doctor.Id, null, now, "seed", now);
        visit.UpdateDraft(new(null, null, "SensitiveDiagnosis", "SensitiveNotes", null, null, null), "seed", now);
        var medication = new Medication("SensitiveMedication-" + Guid.NewGuid().ToString("N"), null, null, null,
            "500", "mg", DosageForm.Tablet, MedicationRoute.Oral, null, "seed", now);
        var prescription = new Prescription(visit.Id, patient.Id, doctor.Id, "SensitivePrescriptionNotes", null, "seed", now);
        prescription.AddItem(medication, "1 tablet", "daily", "5 days", "SensitiveInstructions", null, "seed", now);
        prescription.FinalizePrescription(new Dictionary<Guid, bool> { [medication.Id] = true }, "seed", now);
        var appointment = new Appointment(patient.Id, doctor.Id, now.AddDays(2), now.AddDays(2).AddMinutes(30), AppointmentType.Consultation);
        await using (var db = database.CreateContext())
        {
            db.AddRange(patient, doctor, visit, medication, prescription, appointment);
            await db.SaveChangesAsync();
        }
        var failure = new FailAuditInsert();
        var factory = new BookingApiFactory(database.ConnectionString).WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => services.AddDbContext<ClinicDbContext>(options => options.AddInterceptors(failure))));
        return new(factory, failure, patient, doctor, visit, prescription, appointment);
    }

    private static readonly string[] ReadPermissions = ["patients.admin.read", "patients.clinical.read", "visits.read", "prescriptions.read", "appointments.reschedule"];
    private static (HttpClient Client, string Actor) Client(Harness h, string[]? permissions = null,
        Guid? patientScope = null, string role = "Doctor", bool mfa = true)
    {
        var claims = new List<Claim> { new(ClaimTypes.Role, role), new("patient_record_id", (patientScope ?? h.Patient.Id).ToString()),
            new("appointment_doctor_id", h.Doctor.Id.ToString()) };
        if (mfa) claims.Add(new("amr", "mfa"));
        claims.AddRange((permissions ?? ReadPermissions).Select(p => new Claim("permission", p)));
        var persisted = PersistedTicketStaff.Add(h.Factory, claims).ToList();
        var actor = persisted.Single(x => x.Type == "staff_id").Value;
        persisted.Add(new(ClaimTypes.NameIdentifier, actor));
        var options = h.Factory.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get("ClinicStaff");
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(persisted, "ClinicStaff")),
            new AuthenticationProperties { IssuedUtc = DateTimeOffset.UtcNow, ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(10) }, "ClinicStaff");
        var client = h.Factory.CreateClient(new() { BaseAddress = new("https://localhost"), AllowAutoRedirect = false, HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"{options.Cookie.Name}={options.TicketDataFormat.Protect(ticket)}");
        return (client, actor);
    }

    [Theory]
    [InlineData("patient", "patient.record.read", "patient")]
    [InlineData("profile", "patient.clinical-profile.read", "patient")]
    [InlineData("visit", "visit.read", "visit")]
    [InlineData("visits", "patient.visits.read", "patient")]
    [InlineData("prescription", "prescription.read", "prescription")]
    [InlineData("prescriptions", "patient.prescriptions.read", "patient")]
    [InlineData("visit-prescriptions", "patient.prescriptions.read", "patient")]
    [InlineData("pdf", "prescription.pdf.download", "prescription")]
    [InlineData("appointment", "appointment.read", "appointment")]
    public async Task SensitiveReadPersistsOneEventAndFailsClosedOnSqlAuditFailure(string kind, string action, string type)
    {
        using var h = await Seed();
        var (client, actor) = Client(h);
        using var ownedClient = client;
        using var success = await client.GetAsync(h.Path(kind));
        Assert.Equal(HttpStatusCode.OK, success.StatusCode);
        Assert.True(success.Headers.CacheControl?.NoStore);
        await using var verify = database.CreateContext();
        var e = Assert.Single(await verify.AuditEvents.Where(x => x.ActorStaffId == actor).ToListAsync());
        Assert.Equal(action, e.ActionCode);
        Assert.Equal(type, e.ResourceType);
        Assert.Equal(h.Patient.Id, e.PatientId);
        var resource = type switch { "visit" => h.Visit.Id, "prescription" => h.Prescription.Id, "appointment" => h.Appointment.Id, _ => h.Patient.Id };
        Assert.Equal(resource.ToString("N"), e.ResourceId);
        Assert.Equal(AuditOutcome.Succeeded, e.Outcome);
        Assert.False(string.IsNullOrWhiteSpace(e.TraceId));
        Assert.InRange(e.TraceId!.Length, 1, 128);
        if (kind == "pdf") Assert.Equal("en", Assert.Single(e.Metadata).Value);
        else Assert.Empty(e.Metadata);
        h.Failure.Enabled = true;
        using var failed = await client.GetAsync(h.Path(kind));
        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        Assert.Equal("application/problem+json", failed.Content.Headers.ContentType?.MediaType);
        var text = await failed.Content.ReadAsStringAsync();
        Assert.Equal("unexpected_error", JsonDocument.Parse(text).RootElement.GetProperty("code").GetString());
        Assert.DoesNotContain("Sensitive", text);
        Assert.DoesNotContain("Synthetic audit outage", text);
        Assert.DoesNotContain("%PDF", text);
        Assert.Null(failed.Content.Headers.ContentDisposition);
        Assert.Equal(2, h.Failure.Attempts);
        Assert.Single(await verify.AuditEvents.Where(x => x.ActorStaffId == actor).ToListAsync());
        var saved = await verify.Set<Prescription>().AsNoTracking().SingleAsync(x => x.Id == h.Prescription.Id);
        Assert.Equal(PrescriptionStatus.Finalized, saved.Status);
        Assert.Equal(h.Prescription.RowVersion, saved.RowVersion);
        Assert.Null(saved.ReleasedAtUtc);
    }

    [Theory]
    [InlineData("patient")]
    [InlineData("profile")]
    [InlineData("visit")]
    [InlineData("prescription")]
    [InlineData("pdf")]
    public async Task ReadAuthorizationPrecedesAuditAndPersistedRevocationIsImmediate(string kind)
    {
        using var h = await Seed();
        h.Failure.Enabled = true; // Any accidental successful-access audit would turn these denials into 500.
        using var anonymous = h.Factory.CreateClient(new() { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(h.Path(kind))).StatusCode);
        var (nonMfa, _) = Client(h, mfa: false);
        using (nonMfa) Assert.Equal(HttpStatusCode.Forbidden, (await nonMfa.GetAsync(h.Path(kind))).StatusCode);
        var (missingPermission, _) = Client(h, permissions: []);
        using (missingPermission) Assert.Equal(HttpStatusCode.Forbidden, (await missingPermission.GetAsync(h.Path(kind))).StatusCode);
        var (wrongScope, _) = Client(h, patientScope: Guid.NewGuid());
        using (wrongScope) Assert.Equal(HttpStatusCode.Forbidden, (await wrongScope.GetAsync(h.Path(kind))).StatusCode);
        var (revoked, actor) = Client(h);
        using (revoked)
        {
            await using var db = database.CreateContext();
            var grant = await db.UserClaims.FirstAsync(x => x.UserId == actor && x.ClaimType == "permission");
            db.Remove(grant);
            await db.SaveChangesAsync();
            Assert.Equal(HttpStatusCode.Unauthorized, (await revoked.GetAsync(h.Path(kind))).StatusCode);
        }
        Assert.Equal(0, h.Failure.Attempts);
    }

    [Theory]
    [InlineData("visit")]
    [InlineData("prescription")]
    [InlineData("pdf")]
    public async Task ForeignResourceRemainsHiddenWithoutAnAuditAttempt(string kind)
    {
        using var h = await Seed();
        var otherPatient = Guid.NewGuid();
        var (client, _) = Client(h, patientScope: otherPatient);
        using (client)
        {
            h.Failure.Enabled = true;
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(h.Path(kind).Replace(h.Patient.Id.ToString(), otherPatient.ToString()))).StatusCode);
            Assert.Equal(0, h.Failure.Attempts);
        }
    }

    [Theory]
    [InlineData("en")]
    [InlineData("ar")]
    public async Task RepeatedPdfDownloadsAreSeparateEventsWithoutLifecycleChanges(string language)
    {
        using var h = await Seed();
        var (client, actor) = Client(h);
        using (client)
        {
            for (var i = 0; i < 2; i++)
            {
                using var response = await client.GetAsync(h.Path("pdf").Replace("language=en", "language=" + language));
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.Equal("application/pdf", response.Content.Headers.ContentType?.MediaType);
                var bytes = await response.Content.ReadAsByteArrayAsync();
                Assert.StartsWith("%PDF-", Encoding.ASCII.GetString(bytes, 0, 8));
                Assert.Contains("%%EOF", Encoding.ASCII.GetString(bytes));
            }
        }
        await using var db = database.CreateContext();
        var events = await db.AuditEvents.Where(x => x.ActorStaffId == actor).ToListAsync();
        Assert.Equal(2, events.Count);
        Assert.All(events, e => { Assert.Equal("prescription.pdf.download", e.ActionCode); Assert.Equal(language, e.Metadata["language"]); Assert.Single(e.Metadata); });
        var saved = await db.Set<Prescription>().SingleAsync(x => x.Id == h.Prescription.Id);
        Assert.Equal(h.Prescription.RowVersion, saved.RowVersion);
        Assert.Equal(PrescriptionStatus.Finalized, saved.Status);
        Assert.Null(saved.ReleasedAtUtc);
    }

    [Fact]
    public async Task QueryFiltersScopesAndAdministrativePrivilegeBeforePagination()
    {
        using var h = await Seed();
        var now = DateTimeOffset.UtcNow.AddSeconds(-1);
        var marker = Guid.NewGuid().ToString("N");
        await using (var db = database.CreateContext())
        {
            for (var i = 0; i < 3; i++) db.Add(new AuditEvent("seed", "test.query", "test-resource", marker, h.Patient.Id, AuditOutcome.Succeeded, null, null, now));
            db.Add(new AuditEvent("seed", "test.query", "test-resource", marker, Guid.NewGuid(), AuditOutcome.Succeeded, null, null, now));
            db.Add(new AuditEvent("seed", "test.query", "test-resource", marker, null, AuditOutcome.Succeeded, null, null, now));
            await db.SaveChangesAsync();
        }
        var (patientClient, actor) = Client(h, ["audit.patient.read"]);
        using (patientClient)
        {
            var url = $"/api/staff/audit-events?actionCode=test.query&resourceType=test-resource&resourceId={marker}&actorStaffId=seed&outcome=Succeeded&pageSize=2";
            using var first = await patientClient.GetAsync(url);
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
            var firstJson = JsonDocument.Parse(await first.Content.ReadAsStringAsync()).RootElement;
            Assert.True(firstJson.GetProperty("hasMore").GetBoolean());
            var firstRows = firstJson.GetProperty("items").EnumerateArray().ToArray();
            Assert.Equal(2, firstRows.Length);
            using var second = await patientClient.GetAsync(url + "&page=2");
            var secondJson = JsonDocument.Parse(await second.Content.ReadAsStringAsync()).RootElement;
            Assert.False(secondJson.GetProperty("hasMore").GetBoolean());
            var last = Assert.Single(secondJson.GetProperty("items").EnumerateArray());
            var actualIds = firstRows.Append(last).Select(x => x.GetProperty("id").GetGuid()).ToArray();
            await using var verify = database.CreateContext();
            var expectedIds = await verify.AuditEvents.Where(x => x.PatientId == h.Patient.Id && x.ResourceId == marker)
                .OrderByDescending(x => x.OccurredAtUtc).ThenByDescending(x => x.Id).Select(x => x.Id).ToArrayAsync();
            Assert.Equal(expectedIds, actualIds);
            Assert.All(firstRows.Append(last), row => Assert.Equal(h.Patient.Id, row.GetProperty("patientId").GetGuid()));
            Assert.Equal(HttpStatusCode.Forbidden, (await patientClient.GetAsync(url + "&patientId=" + Guid.NewGuid())).StatusCode);
            var accessEvents = await verify.AuditEvents.Where(x => x.ActorStaffId == actor).ToListAsync();
            Assert.Equal(2, accessEvents.Count);
            Assert.All(accessEvents, e => { Assert.Equal("audit.query", e.ActionCode); Assert.Equal("audit", e.ResourceType); Assert.Equal("events", e.ResourceId); Assert.Null(e.PatientId); Assert.Empty(e.Metadata); });
            h.Failure.Enabled = true;
            using var failed = await patientClient.GetAsync(url);
            Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
            Assert.DoesNotContain(marker, await failed.Content.ReadAsStringAsync());
            h.Failure.Enabled = false;
        }
        var (adminClient, _) = Client(h, ["audit.admin.read"]);
        using (adminClient)
        {
            using var response = await adminClient.GetAsync($"/api/staff/audit-events?resourceId={marker}");
            var row = Assert.Single(JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("items").EnumerateArray());
            Assert.Equal(JsonValueKind.Null, row.GetProperty("patientId").ValueKind);
            Assert.Equal(HttpStatusCode.Forbidden, (await adminClient.GetAsync("/api/staff/audit-events?patientId=" + h.Patient.Id)).StatusCode);
        }
    }

    [Theory]
    [InlineData("page=0")]
    [InlineData("page=1001")]
    [InlineData("pageSize=101")]
    [InlineData("pageSize=0")]
    [InlineData("outcome=999")]
    [InlineData("fromUtc=2020-01-01&toUtc=2026-01-01")]
    [InlineData("fromUtc=2026-02-01&toUtc=2026-01-01")]
    public async Task InvalidAuditQueryIsBoundedBeforeAnyWrite(string query)
    {
        using var h = await Seed();
        var (client, _) = Client(h, ["audit.patient.read"]);
        using (client) Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/staff/audit-events?" + query)).StatusCode);
        Assert.Equal(0, h.Failure.Attempts);
    }

    [Fact]
    public async Task AuditPermissionRoleMfaAndPersistedPatientScopeAreRequired()
    {
        using var h = await Seed();
        using var anonymous = h.Factory.CreateClient(new() { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/staff/audit-events")).StatusCode);
        var (noPermission, _) = Client(h);
        using (noPermission) Assert.Equal(HttpStatusCode.Forbidden, (await noPermission.GetAsync("/api/staff/audit-events")).StatusCode);
        var (wrongRole, _) = Client(h, ["audit.admin.read"], role: "Receptionist");
        using (wrongRole) Assert.Equal(HttpStatusCode.Forbidden, (await wrongRole.GetAsync("/api/staff/audit-events")).StatusCode);
        var (noMfa, _) = Client(h, ["audit.admin.read"], mfa: false);
        using (noMfa) Assert.Equal(HttpStatusCode.Forbidden, (await noMfa.GetAsync("/api/staff/audit-events")).StatusCode);
        var (scoped, actor) = Client(h, ["audit.patient.read"]);
        using (scoped)
        {
            Assert.Equal(HttpStatusCode.OK, (await scoped.GetAsync("/api/staff/audit-events?patientId=" + h.Patient.Id)).StatusCode);
            await using var db = database.CreateContext();
            var e = await db.AuditEvents.SingleAsync(x => x.ActorStaffId == actor);
            Assert.Equal(h.Patient.Id, e.PatientId);
            db.Remove(await db.UserClaims.SingleAsync(x => x.UserId == actor && x.ClaimType == "patient_record_id"));
            await db.SaveChangesAsync();
            Assert.Equal(HttpStatusCode.Unauthorized, (await scoped.GetAsync("/api/staff/audit-events")).StatusCode);
        }
    }
}
