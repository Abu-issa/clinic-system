using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Clinic.Application.ClinicalTests;
using Clinic.Domain.Entities;
using Clinic.Domain.Enums;
using Clinic.Infrastructure.Persistence;
using Clinic.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Clinic.IntegrationTests.Api;

public sealed partial class StaffClinicalTestsHttpTests(SqlDatabaseFixture database) : IClassFixture<SqlDatabaseFixture>
{
    private static readonly DateTimeOffset ServerNow = new(2026, 9, 17, 8, 30, 0, TimeSpan.Zero);
    private sealed class FixedClock : TimeProvider { public override DateTimeOffset GetUtcNow() => ServerNow; }

    private sealed class Harness(WebApplicationFactory<Program> factory, AccessAuditHttpTests.FailAuditInsert failure,
        Patient patient, Doctor doctor, Visit visit) : IDisposable
    {
        public WebApplicationFactory<Program> Factory { get; } = factory;
        public AccessAuditHttpTests.FailAuditInsert Failure { get; } = failure;
        public Patient Patient { get; } = patient;
        public Doctor Doctor { get; } = doctor;
        public Visit Visit { get; } = visit;
        public string PatientPath => $"/api/staff/patients/{Patient.Id}";
        public void Dispose() => Factory.Dispose();
    }

    private async Task<Harness> Seed()
    {
        var now = DateTimeOffset.UtcNow;
        var patient = new Patient("SensitiveName", "SensitivePhone");
        var doctor = new Doctor("Synthetic doctor");
        var visit = new Visit(patient.Id, doctor.Id, null, now, "seed", now);
        await using (var db = database.CreateContext())
        {
            db.AddRange(patient, doctor, visit);
            await db.SaveChangesAsync();
        }

        var failure = new AccessAuditHttpTests.FailAuditInsert();
        var factory = new BookingApiFactory(database.ConnectionString).WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
                services.AddSingleton<TimeProvider>(new FixedClock()).AddDbContext<ClinicDbContext>(options => options.AddInterceptors(failure))));
        return new Harness(factory, failure, patient, doctor, visit);
    }

    private static readonly string[] ReadPermissions = ["tests.read", "tests.write"];

    /// <summary>Ticket-backed client. When associateDoctor is set, the persisted staff user's
    /// AssociatedDoctorId is pointed at the seeded doctor (real clinical authority).</summary>
    private async Task<(HttpClient Client, string Actor)> Client(Harness h, string role = "Doctor",
        Guid? patientScope = null, string[]? permissions = null, bool mfa = true, bool associateDoctor = true, Claim[]? extraClaims = null)
    {
        var claims = new List<Claim> { new(ClaimTypes.Role, role),
            new("patient_record_id", (patientScope ?? h.Patient.Id).ToString()) };
        if (extraClaims is not null) claims.AddRange(extraClaims);
        if (mfa) claims.Add(new("amr", "mfa"));
        claims.AddRange((permissions ?? ReadPermissions).Select(p => new Claim("permission", p)));
        var persisted = PersistedTicketStaff.Add(h.Factory, claims).ToList();
        var actor = persisted.Single(x => x.Type == "staff_id").Value;
        persisted.Add(new(ClaimTypes.NameIdentifier, actor));
        if (associateDoctor)
        {
            await using var db = database.CreateContext();
            var user = await db.Users.SingleAsync(x => x.Id == actor);
            user.AssociatedDoctorId = h.Doctor.Id;
            await db.SaveChangesAsync();
        }
        var authOptions = h.Factory.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get("ClinicStaff");
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(persisted, "ClinicStaff")),
            new AuthenticationProperties { IssuedUtc = ServerNow, ExpiresUtc = ServerNow.AddMinutes(10) },
            "ClinicStaff");
        var client = h.Factory.CreateClient(new() { BaseAddress = new("https://localhost"), AllowAutoRedirect = false, HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"{authOptions.Cookie.Name}={authOptions.TicketDataFormat.Protect(ticket)}");
        return (client, actor);
    }

    private static async Task<HttpClient> WithCsrf(HttpClient client)
    {
        using var response = await client.GetAsync("/api/staff/auth/csrf");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var token = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement
            .GetProperty("requestToken").GetString();
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", token);
        // The clients intentionally do not run a cookie container; carry the antiforgery
        // cookie explicitly alongside the staff session cookie.
        var setCookie = response.Headers.TryGetValues("Set-Cookie", out var cookies)
            ? cookies.First(c => c.StartsWith("__Host-Clinic.Antiforgery=", StringComparison.Ordinal))
            : null;
        var antiforgeryValue = setCookie?.Split(';', 2)[0];
        if (antiforgeryValue is not null)
        {
            var existing = client.DefaultRequestHeaders.TryGetValues("Cookie", out var current)
                ? current.Single() : null;
            client.DefaultRequestHeaders.Remove("Cookie");
            client.DefaultRequestHeaders.Add("Cookie", $"{existing}; {antiforgeryValue}");
        }
        return client;
    }

    private static object Body(Guid? visitId = null, string category = "Lab", string name = "SensitiveCBC فحص", string? instructions = "SensitiveInstructions") =>
        new { category, testName = name, visitId, clinicalInstructions = instructions };
    private static async Task<Guid> CreateOrder(HttpClient client, Harness h, Guid? visit = null, string category = "Lab")
    {
        using var response = await client.PostAsJsonAsync($"{h.PatientPath}/test-requests", Body(visit, category));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        return json.GetProperty("id").GetGuid();
    }

    [Theory]
    [InlineData("Lab", false)]
    [InlineData("Imaging", true)]
    public async Task CreateUsesServerAuthorityTimeAndRequestedStateWithOneAtomicAudit(string category, bool visitLinked)
    {
        using var h = await Seed();
        var (client, actor) = await Client(h);
        using var owned = client;
        await WithCsrf(client);
        using var response = await client.PostAsJsonAsync($"{h.PatientPath}/test-requests", new
        {
            category, testName = "CBC فحص", clinicalInstructions = "SensitiveInstructions",
            visitId = visitLinked ? (Guid?)h.Visit.Id : null,
            requestedByDoctorId = Guid.NewGuid(), status = "Reviewed", requestedAtUtc = ServerNow.AddYears(-1),
            reviewedByDoctorId = Guid.NewGuid(), uploadedAtUtc = ServerNow,
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(text).RootElement;
        var id = json.GetProperty("id").GetGuid();
        Assert.Equal("Requested", json.GetProperty("status").GetString());
        Assert.Equal(category, json.GetProperty("category").GetString());
        Assert.Equal(ServerNow, json.GetProperty("requestedAtUtc").GetDateTimeOffset());
        foreach (var hidden in new[] { "rowVersion", "requestedByDoctorId", "clinicalInstructions", "SensitiveName", "staff" })
            Assert.DoesNotContain(hidden, text);
        await using var fresh = database.CreateContext();
        var request = await fresh.ClinicalTestRequests.SingleAsync(x => x.Id == id);
        Assert.Equal(h.Doctor.Id, request.RequestedByDoctorId);
        Assert.Equal(visitLinked ? (Guid?)h.Visit.Id : null, request.VisitId);
        Assert.Equal(8, request.RowVersion.Length);
        Assert.Null(request.UploadedAtUtc);
        Assert.Null(request.ReviewedAtUtc);
        var audit = Assert.Single(await fresh.AuditEvents.Where(x => x.PatientId == h.Patient.Id).ToListAsync());
        Assert.Equal("test-request.create", audit.ActionCode);
        Assert.Equal("test-request", audit.ResourceType);
        Assert.Equal(id.ToString("N"), audit.ResourceId);
        Assert.Equal(actor, audit.ActorStaffId);
        Assert.Equal(AuditOutcome.Succeeded, audit.Outcome);
        Assert.Empty(audit.Metadata);
    }

    [Theory]
    [InlineData("Doctor", false, true, true)]
    [InlineData("Doctor", true, false, true)]
    [InlineData("Doctor", true, true, false)]
    [InlineData("DoctorAssistant", true, true, true)]
    [InlineData("Receptionist", true, true, true)]
    public async Task CreateEnforcesRoleMfaPermissionAndExactPatientScope(string role, bool mfa, bool permission, bool scope)
    {
        using var h = await Seed();
        var (client, _) = await Client(h, role: role, mfa: mfa, permissions: permission ? ["tests.write"] : [],
            patientScope: scope ? h.Patient.Id : Guid.NewGuid());
        using var owned = client;
        await WithCsrf(client);
        using var response = await client.PostAsJsonAsync($"{h.PatientPath}/test-requests", Body());
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await AssertNoMutation(h);
    }

    private async Task AssertNoMutation(Harness h)
    {
        await using var fresh = database.CreateContext();
        Assert.False(await fresh.ClinicalTestRequests.AnyAsync(x => x.PatientId == h.Patient.Id));
        Assert.False(await fresh.AuditEvents.AnyAsync(x => x.PatientId == h.Patient.Id));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("wrong")]
    [InlineData("appointment")]
    [InlineData("schedule")]
    public async Task VisitRequiresLiveAssociatedDoctorEvenWithOtherDoctorGrants(string authority)
    {
        using var h = await Seed();
        var claims = authority == "appointment" ? new[] { new Claim("appointment_doctor_id", h.Doctor.Id.ToString()) }
            : authority == "schedule" ? new[] { new Claim("schedule_doctor_id", h.Doctor.Id.ToString()) } : [];
        var (client, actor) = await Client(h, extraClaims: claims);
        using var owned = client;
        await WithCsrf(client);
        var other = new Doctor("Other doctor");
        await using (var db = database.CreateContext())
        {
            db.Add(other);
            var user = await db.Users.SingleAsync(x => x.Id == actor);
            user.AssociatedDoctorId = authority == "null" ? null : other.Id;
            await db.SaveChangesAsync();
        }
        using var response = await client.PostAsJsonAsync($"{h.PatientPath}/test-requests?doctorId={h.Doctor.Id}", Body(h.Visit.Id));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await AssertNoMutation(h);
    }

    [Fact]
    public async Task StandaloneRequiresPersistedDoctorAssociation()
    {
        using var h = await Seed();
        var (client, _) = await Client(h, associateDoctor: false);
        using var owned = client;
        await WithCsrf(client);
        using var response = await client.PostAsJsonAsync($"{h.PatientPath}/test-requests", Body());
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await AssertNoMutation(h);
    }

    [Fact]
    public async Task CrossPatientVisitIsHiddenBeforeMutation()
    {
        using var h = await Seed();
        var other = new Patient("Other", "Other");
        var visit = new Visit(other.Id, h.Doctor.Id, null, ServerNow, "seed", ServerNow);
        await using (var db = database.CreateContext()) { db.AddRange(other, visit); await db.SaveChangesAsync(); }
        var (client, _) = await Client(h);
        using var owned = client;
        await WithCsrf(client);
        using var response = await client.PostAsJsonAsync($"{h.PatientPath}/test-requests", Body(visit.Id));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertNoMutation(h);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrInvalidCsrfCannotCreate(bool invalid)
    {
        using var h = await Seed();
        var (client, _) = await Client(h);
        using var owned = client;
        if (invalid) { await WithCsrf(client); client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN"); client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", "forged"); }
        using var response = await client.PostAsJsonAsync($"{h.PatientPath}/test-requests", Body());
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertNoMutation(h);
    }

    [Theory]
    [InlineData("blank")]
    [InlineData("name-long")]
    [InlineData("instructions-long")]
    [InlineData("control")]
    [InlineData("instruction-control")]
    [InlineData("category")]
    [InlineData("visit-empty")]
    public async Task InvalidInputCannotCreateOrAudit(string invalid)
    {
        using var h = await Seed();
        var (client, _) = await Client(h);
        using var owned = client;
        await WithCsrf(client);
        using var response = await client.PostAsJsonAsync($"{h.PatientPath}/test-requests", Body(
            invalid == "visit-empty" ? Guid.Empty : null, invalid == "category" ? "Unknown" : "Lab",
            invalid == "blank" ? " " : invalid == "name-long" ? new string('a', 201) : invalid == "control" ? "CBC\n" : "CBC",
            invalid == "instructions-long" ? new string('a', 2001) : invalid == "instruction-control" ? "hello\0" : null));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertNoMutation(h);
    }

    [Fact]
    public async Task AuditInsertFailureRollsBackOrderAndEvent()
    {
        using var h = await Seed();
        var (client, _) = await Client(h);
        using var owned = client;
        await WithCsrf(client);
        h.Failure.Enabled = true;
        using var response = await client.PostAsJsonAsync($"{h.PatientPath}/test-requests", Body());
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.True(h.Failure.Attempts > 0);
        var text = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Sensitive", text);
        Assert.Contains("unexpected_error", text);
        await AssertNoMutation(h);
    }
    [Theory]
    [InlineData("Doctor")]
    [InlineData("DoctorAssistant")]
    public async Task AuthorizedHistoryAndDetailHaveOneEventPerReadAndSafeProjection(string role)
    {
        using var h = await Seed();
        var (writer, _) = await Client(h);
        using var ownedWriter = writer;
        await WithCsrf(writer);
        var lab = await CreateOrder(writer, h);
        await CreateOrder(writer, h, h.Visit.Id, "Imaging");
        var (reader, actor) = await Client(h, role: role, permissions: ["tests.read"]);
        using var ownedReader = reader;
        using var list = await reader.GetAsync($"{h.PatientPath}/test-requests?category=Lab&status=Requested&page=1&pageSize=1");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var listText = await list.Content.ReadAsStringAsync();
        var items = JsonDocument.Parse(listText).RootElement.GetProperty("items");
        Assert.Equal(lab, Assert.Single(items.EnumerateArray()).GetProperty("id").GetGuid());
        Assert.DoesNotContain("clinicalInstructions", listText);
        using var detail = await reader.GetAsync($"{h.PatientPath}/test-requests/{lab}");
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        var text = await detail.Content.ReadAsStringAsync();
        Assert.Contains("SensitiveInstructions", text);
        Assert.Equal(8, jsonVersion(text).Length);
        foreach (var hidden in new[] { "requestedByDoctorId", "SensitiveName", "storageKey", "staff" })
            Assert.DoesNotContain(hidden, text);
        Assert.True(detail.Headers.CacheControl?.NoStore);
        await using var db = database.CreateContext();
        var events = await db.AuditEvents.Where(x => x.PatientId == h.Patient.Id && x.ActorStaffId == actor).ToListAsync();
        var listEvent = Assert.Single(events, x => x.ActionCode == "test-request.list");
        Assert.Equal("patient", listEvent.ResourceType);
        Assert.Equal(h.Patient.Id.ToString("N"), listEvent.ResourceId);
        var readEvent = Assert.Single(events, x => x.ActionCode == "test-request.read");
        Assert.Equal("test-request", readEvent.ResourceType);
        Assert.Equal(lab.ToString("N"), readEvent.ResourceId);
        Assert.All(events, x => Assert.Empty(x.Metadata));
        Assert.Equal(2, events.Count);
    }

    [Theory]
    [InlineData("Doctor", false, true, true)]
    [InlineData("Doctor", true, false, true)]
    [InlineData("Doctor", true, true, false)]
    [InlineData("Receptionist", true, true, true)]
    public async Task ReadsEnforceRoleMfaPermissionScopeWithoutAudit(string role, bool mfa, bool permission, bool scope)
    {
        using var h = await Seed();
        var (client, _) = await Client(h, role: role, mfa: mfa, permissions: permission ? ["tests.read"] : [],
            patientScope: scope ? h.Patient.Id : Guid.NewGuid());
        using var owned = client;
        foreach (var suffix in new[] { "", "/" + Guid.NewGuid() })
        {
            using var response = await client.GetAsync($"{h.PatientPath}/test-requests{suffix}");
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
        await AssertNoMutation(h);
    }

    [Fact]
    public async Task KnownForeignRequestIsHiddenAndForeignRowsNeverEnterHistory()
    {
        using var a = await Seed();
        using var b = await Seed();
        var (writer, _) = await Client(b);
        using var ownedWriter = writer;
        await WithCsrf(writer);
        var foreignId = await CreateOrder(writer, b);
        var (reader, actor) = await Client(a);
        using var ownedReader = reader;
        using var hidden = await reader.GetAsync($"{a.PatientPath}/test-requests/{foreignId}");
        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        var text = await hidden.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Sensitive", text);
        Assert.DoesNotContain(foreignId.ToString(), text);
        using var list = await reader.GetAsync($"{a.PatientPath}/test-requests");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.Equal(0, JsonDocument.Parse(await list.Content.ReadAsStringAsync()).RootElement.GetProperty("items").GetArrayLength());
        using var deniedRoute = await reader.GetAsync($"{b.PatientPath}/test-requests");
        Assert.Equal(HttpStatusCode.Forbidden, deniedRoute.StatusCode);
        await using var db = database.CreateContext();
        var audit = Assert.Single(await db.AuditEvents.Where(x => x.ActorStaffId == actor).ToListAsync());
        Assert.Equal("test-request.list", audit.ActionCode);
        Assert.Equal(a.Patient.Id, audit.PatientId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadAuditFailureDisclosesNoClinicalData(bool detail)
    {
        using var h = await Seed();
        var (client, _) = await Client(h);
        using var owned = client;
        await WithCsrf(client);
        var id = await CreateOrder(client, h);
        h.Failure.Enabled = true;
        using var response = await client.GetAsync($"{h.PatientPath}/test-requests" + (detail ? $"/{id}" : ""));
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.Contains("unexpected_error", text);
        Assert.DoesNotContain("Sensitive", text);
        Assert.DoesNotContain(id.ToString(), text);
        Assert.True(response.Headers.CacheControl?.NoStore);
        await using var db = database.CreateContext();
        var audit = Assert.Single(await db.AuditEvents.Where(x => x.PatientId == h.Patient.Id).ToListAsync());
        Assert.Equal("test-request.create", audit.ActionCode);
    }

    [Theory]
    [InlineData("permission", "tests.read", false)]
    [InlineData("permission", "tests.write", true)]
    [InlineData("patient_record_id", null, false)]
    [InlineData("patient_record_id", null, true)]
    public async Task GrantRemovalImmediatelyInvalidatesExistingCookie(string type, string? value, bool write)
    {
        using var h = await Seed();
        var (client, actor) = await Client(h);
        using var owned = client;
        await WithCsrf(client);
        await using (var db = database.CreateContext())
        {
            var claim = await db.UserClaims.SingleAsync(x => x.UserId == actor && x.ClaimType == type && (value == null || x.ClaimValue == value));
            db.UserClaims.Remove(claim); await db.SaveChangesAsync();
        }
        using var response = write ? await client.PostAsJsonAsync($"{h.PatientPath}/test-requests", Body())
            : await client.GetAsync($"{h.PatientPath}/test-requests");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await AssertNoMutation(h);
    }

    [Theory]
    [InlineData("page=0")]
    [InlineData("page=1001")]
    [InlineData("pageSize=0")]
    [InlineData("pageSize=101")]
    [InlineData("page=2147483647")]
    [InlineData("category=999")]
    [InlineData("status=999")]
    [InlineData("status=Unknown")]
    public async Task InvalidHistoryQueryIsRejectedWithoutAudit(string query)
    {
        using var h = await Seed();
        var (client, _) = await Client(h);
        using var owned = client;
        using var response = await client.GetAsync($"{h.PatientPath}/test-requests?{query}");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertNoMutation(h);
    }

    [Fact]
    public async Task EmptyAndMissingPatientsAreRejectedAndNoMutationEndpointsExist()
    {
        using var h = await Seed();
        var missingPatient = Guid.NewGuid();
        var (client, _) = await Client(h, patientScope: missingPatient);
        using var owned = client;
        await WithCsrf(client);
        using var missing = await client.PostAsJsonAsync($"/api/staff/patients/{missingPatient}/test-requests", Body());
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        using var empty = await client.PostAsJsonAsync($"/api/staff/patients/{Guid.Empty}/test-requests", Body());
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        foreach (var method in new[] { HttpMethod.Put, HttpMethod.Patch, HttpMethod.Delete })
        {
            using var request = new HttpRequestMessage(method, $"{h.PatientPath}/test-requests/{Guid.NewGuid()}");
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        }
        await AssertNoMutation(h);
    }
}
