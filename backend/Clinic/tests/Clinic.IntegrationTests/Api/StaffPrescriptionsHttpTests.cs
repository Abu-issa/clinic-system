using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Clinic.Domain.Entities;
using Clinic.Domain.Enums;
using Clinic.Infrastructure.Authentication;
using Clinic.Infrastructure.Persistence;
using Clinic.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Clinic.IntegrationTests.Api;

// Real HTTP tests for prescription endpoints: persisted authorization, per-request doctor
// authority, CSRF, patient scope, optimistic concurrency, and the Phase 1 lifecycle via HTTP.
public sealed class StaffPrescriptionsHttpTests : IAsyncLifetime
{
    private sealed class CaptureLogs : ILoggerProvider
    {
        public System.Collections.Concurrent.ConcurrentBag<string> Messages { get; } = [];
        public ILogger CreateLogger(string categoryName) => new Sink(Messages);
        public void Dispose() { }
        private sealed class Sink(System.Collections.Concurrent.ConcurrentBag<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                messages.Add(formatter(state, exception) + (exception is null ? "" : Environment.NewLine + exception));
        }
    }

    private const string Password = "Synthetic-Prescription-Password-99!";
    private const string DoctorName = "synthetic-rx-doctor";
    private const string OtherDoctorName = "synthetic-rx-other-doctor";
    private const string AssistantName = "synthetic-rx-assistant";
    private const string ReceptionistName = "synthetic-rx-receptionist";
    private static readonly string[] DoctorPermissions =
        ["medications.read", "medications.manage", "prescriptions.read", "prescriptions.write", "prescriptions.finalize", "prescriptions.release", "prescriptions.cancel"];
    private readonly SqlDatabaseFixture _database = new();
    private readonly CaptureLogs _logs = new();
    private WebApplicationFactory<Program> _factory = null!;
    private Guid _doctorEntityId;
    private Guid _otherDoctorEntityId;
    private Guid _patientId;
    private string _doctorUserId = null!;
    private string _otherDoctorUserId = null!;
    private string _assistantUserId = null!;
    private string _receptionistUserId = null!;

    public async Task InitializeAsync()
    {
        await _database.InitializeAsync();
        _factory = new BookingApiFactory(_database.ConnectionString).WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
                services.AddSingleton<ILoggerProvider>(_logs)));

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ClinicDbContext>();
        var admin = scope.ServiceProvider.GetRequiredService<StaffAdministration>();
        var users = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<StaffUser>>();

        var doctor = new Doctor("Synthetic Prescription Doctor");
        var otherDoctor = new Doctor("Synthetic Other Doctor");
        var patient = new Patient("Synthetic Prescription Patient", "shared-phone");
        db.AddRange(doctor, otherDoctor, patient);
        await db.SaveChangesAsync();
        _doctorEntityId = doctor.Id;
        _otherDoctorEntityId = otherDoctor.Id;
        _patientId = patient.Id;

        _doctorUserId = await admin.ProvisionFirstDoctorAsync(DoctorName, Password, _doctorEntityId, [_doctorEntityId]);
        await admin.ChangeAsync(_doctorUserId, "set-grants",
            approvedRoles: ["Doctor"], permissions: DoctorPermissions, scopes: [_doctorEntityId], patientScopes: [_patientId]);

        // Second doctor: same patient scope and permissions, but a different persisted
        // AssociatedDoctorId and scheduling claims for the first doctor — none of which may
        // grant prescription authority over the first doctor's prescriptions.
        var otherUser = new StaffUser { UserName = OtherDoctorName, LockoutEnabled = true, AssociatedDoctorId = _otherDoctorEntityId };
        Assert.True((await users.CreateAsync(otherUser, Password)).Succeeded);
        _otherDoctorUserId = otherUser.Id;
        await admin.ChangeAsync(_otherDoctorUserId, "set-grants",
            approvedRoles: ["Doctor"], permissions: DoctorPermissions, scopes: [_doctorEntityId], patientScopes: [_patientId]);

        var assistant = new StaffUser { UserName = AssistantName, LockoutEnabled = true, AssociatedDoctorId = _doctorEntityId };
        Assert.True((await users.CreateAsync(assistant, Password)).Succeeded);
        _assistantUserId = assistant.Id;
        await admin.ChangeAsync(_assistantUserId, "set-grants",
            approvedRoles: ["DoctorAssistant"], permissions: DoctorPermissions, scopes: [_doctorEntityId], patientScopes: [_patientId]);

        var receptionist = new StaffUser { UserName = ReceptionistName, LockoutEnabled = true };
        Assert.True((await users.CreateAsync(receptionist, Password)).Succeeded);
        _receptionistUserId = receptionist.Id;
        await admin.ChangeAsync(_receptionistUserId, "set-grants",
            approvedRoles: ["Receptionist"], permissions: DoctorPermissions, scopes: [_doctorEntityId], patientScopes: [_patientId]);
    }

    public async Task DisposeAsync()
    {
        _factory.Dispose();
        await _database.DisposeAsync();
    }

    private HttpClient Client() => _factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress = new Uri("https://localhost"),
        AllowAutoRedirect = false,
        HandleCookies = true
    });

    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    private static void Token(HttpClient client, string token)
    {
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", token);
    }

    private static async Task Csrf(HttpClient client)
    {
        using var response = await client.GetAsync("/api/staff/auth/csrf");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Token(client, (await Json(response)).GetProperty("requestToken").GetString()!);
    }

    private readonly Dictionary<string, string> _totpKeys = new();

    private async Task<HttpClient> EnrolledClient(string userName)
    {
        var client = Client();
        await Csrf(client);
        using var login = await client.PostAsJsonAsync("/api/staff/auth/login", new { userName, password = Password });
        Assert.True(login.IsSuccessStatusCode);
        Token(client, (await Json(login)).GetProperty("csrfToken").GetString()!);
        if (_totpKeys.TryGetValue(userName, out var savedKey))
        {
            // Already enrolled: complete a second-factor login with the persisted key.
            using var totp = await client.PostAsJsonAsync("/api/staff/auth/totp", new { code = Totp(savedKey) });
            Assert.Equal(HttpStatusCode.OK, totp.StatusCode);
            Token(client, (await Json(totp)).GetProperty("csrfToken").GetString()!);
        }
        else
        {
            using var setup = await client.PostAsync("/api/staff/auth/enrollment/setup", null);
            Assert.Equal(HttpStatusCode.OK, setup.StatusCode);
            savedKey = (await Json(setup)).GetProperty("sharedKey").GetString()!;
            _totpKeys[userName] = savedKey;
            using var verify = await client.PostAsJsonAsync("/api/staff/auth/enrollment/verify", new { code = Totp(savedKey) });
            Assert.Equal(HttpStatusCode.OK, verify.StatusCode);
            Token(client, (await Json(verify)).GetProperty("csrfToken").GetString()!);
        }
        return client;
    }

    private static string Totp(string key)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bytes = new List<byte>();
        int bits = 0, buffer = 0;
        foreach (var c in key.TrimEnd('='))
        {
            buffer = (buffer << 5) | alphabet.IndexOf(c);
            bits += 5;
            if (bits >= 8) { bits -= 8; bytes.Add((byte)(buffer >> bits)); }
        }
        var counter = BitConverter.GetBytes(IPAddress.HostToNetworkOrder(DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30));
        var hash = System.Security.Cryptography.HMACSHA1.HashData(bytes.ToArray(), counter);
        var offset = hash[^1] & 15;
        var number = ((hash[offset] & 127) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (number % 1000000).ToString("D6");
    }

    private async Task<Guid> SeedVisitAsync(Clinic.Domain.Entities.Doctor? doctor = null)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ClinicDbContext>();
        var visit = new Visit(_patientId, (doctor ?? db.Doctors.Find(_doctorEntityId))!.Id, null,
            DateTimeOffset.UtcNow, "synthetic-seed", DateTimeOffset.UtcNow);
        db.Add(visit);
        await db.SaveChangesAsync();
        return visit.Id;
    }

    private async Task<Guid> SeedMedicationAsync(string name)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ClinicDbContext>();
        var medication = new Clinic.Domain.Entities.Medication(name, null, null, null, "500", "mg",
            DosageForm.Tablet, MedicationRoute.Oral, null, "synthetic-seed", DateTimeOffset.UtcNow);
        db.Medications.Add(medication);
        await db.SaveChangesAsync();
        return medication.Id;
    }

    private async Task<JsonElement> CreateDraft(HttpClient client, Guid visitId)
    {
        var response = await client.PostAsJsonAsync($"/api/staff/patients/{_patientId}/visits/{visitId}/prescriptions", new { });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await Json(response);
    }

    private async Task<JsonElement> AddItem(HttpClient client, JsonElement draft, Guid medicationId,
        string? dose = "1 tablet", string? frequency = "twice daily", string? duration = "7 days")
    {
        var response = await client.PostAsJsonAsync(
            $"/api/staff/patients/{_patientId}/prescriptions/{draft.GetProperty("id").GetGuid()}/items",
            new { MedicationId = medicationId, Dose = dose, Frequency = frequency, Duration = duration,
                  Instructions = (string?)null, DisplayOrder = (int?)null,
                  ExpectedRowVersion = draft.GetProperty("rowVersion").GetString() });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await Json(response);
    }

    // ---------------- Security ----------------

    [Fact]
    public async Task AnonymousRequestsAreUnauthorized()
    {
        using var client = Client();
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.GetAsync($"/api/staff/patients/{_patientId}/prescriptions")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.PostAsJsonAsync($"/api/staff/patients/{_patientId}/visits/{Guid.NewGuid()}/prescriptions", new { })).StatusCode);
    }

    [Fact]
    public async Task IntermediateNonMfaSessionIsUnauthorized()
    {
        using var client = Client();
        await Csrf(client);
        using var login = await client.PostAsJsonAsync("/api/staff/auth/login", new { userName = DoctorName, password = Password });
        Assert.True(login.IsSuccessStatusCode);
        Token(client, (await Json(login)).GetProperty("csrfToken").GetString()!);

        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.GetAsync($"/api/staff/patients/{_patientId}/prescriptions")).StatusCode);
    }

    [Fact]
    public async Task MissingAndInvalidCsrfAreRejected()
    {
        using var client = await EnrolledClient(DoctorName);
        var visitId = await SeedVisitAsync();

        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        var missing = await client.PostAsJsonAsync($"/api/staff/patients/{_patientId}/visits/{visitId}/prescriptions", new { });
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Equal("invalid_csrf_token", (await Json(missing)).GetProperty("code").GetString());

        Token(client, "not-a-real-token");
        var invalid = await client.PostAsJsonAsync($"/api/staff/patients/{_patientId}/visits/{visitId}/prescriptions", new { });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal("invalid_csrf_token", (await Json(invalid)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task RevokedPersistedPermissionInvalidatesAnOldCookie()
    {
        using var client = await EnrolledClient(DoctorName);

        using (var scope = _factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<StaffUser>>();
            var user = (await users.FindByIdAsync(_doctorUserId))!;
            Assert.True((await users.RemoveClaimAsync(user,
                new System.Security.Claims.Claim("permission", "prescriptions.read"))).Succeeded);
        }

        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.GetAsync($"/api/staff/patients/{_patientId}/prescriptions")).StatusCode);
    }

    [Fact]
    public async Task DisabledAccountCannotUseAnExistingSession()
    {
        using var client = await EnrolledClient(DoctorName);
        using (var scope = _factory.Services.CreateScope())
        {
            var admin = scope.ServiceProvider.GetRequiredService<StaffAdministration>();
            await admin.ChangeAsync(_doctorUserId, "disable");
        }
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.GetAsync($"/api/staff/patients/{_patientId}/prescriptions")).StatusCode);
    }

    [Fact]
    public async Task RevokedSchedulingScopeInvalidatesAnOldCookie()
    {
        // The provisioned doctor's cookie carries appointment_doctor_id/schedule_doctor_id claims;
        // removing the persisted scope claim must invalidate the unchanged cookie.
        using var client = await EnrolledClient(DoctorName);
        using (var scope = _factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<StaffUser>>();
            var user = (await users.FindByIdAsync(_doctorUserId))!;
            var stamp = user.SecurityStamp;
            Assert.True((await users.RemoveClaimAsync(user,
                new System.Security.Claims.Claim("appointment_doctor_id", _doctorEntityId.ToString()))).Succeeded);
            Assert.Equal(stamp, user.SecurityStamp);
        }
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.GetAsync($"/api/staff/patients/{_patientId}/prescriptions")).StatusCode);
    }

    [Fact]
    public async Task RoleRemovalInvalidatesAndRoleAdditionDoesNotElevateAnOldCookie()
    {
        using var doctorClient = await EnrolledClient(DoctorName);

        using (var scope = _factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<StaffUser>>();
            var doctor = (await users.FindByIdAsync(_doctorUserId))!;
            Assert.True((await users.RemoveFromRoleAsync(doctor, "Doctor")).Succeeded);

            // Mixed-role escalation attempt: add the Doctor role to the assistant without any
            // stamp rotation. The old cookie still names only DoctorAssistant, so the prescription
            // policy must keep denying it until a renewed session is established.
            var assistant = (await users.FindByIdAsync(_assistantUserId))!;
            Assert.True((await users.AddToRoleAsync(assistant, "Doctor")).Succeeded);
        }

        // The unchanged doctor cookie names a role that no longer exists persistently.
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await doctorClient.GetAsync($"/api/staff/patients/{_patientId}/prescriptions")).StatusCode);

        // The assistant's renewed session holds both roles, so the read policy passes: the role
        // addition only takes effect through a fresh session, never through the old cookie.
        using var assistantSession = await EnrolledClient(AssistantName);
        var visitId = await SeedVisitAsync();
        var response = await assistantSession.GetAsync($"/api/staff/patients/{_patientId}/visits/{visitId}/prescriptions");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task PatientScopeRemovalInvalidatesAnOldCookie()
    {
        using var client = await EnrolledClient(DoctorName);
        using (var scope = _factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<StaffUser>>();
            var user = (await users.FindByIdAsync(_doctorUserId))!;
            Assert.True((await users.RemoveClaimAsync(user,
                new System.Security.Claims.Claim("patient_record_id", _patientId.ToString()))).Succeeded);
        }
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.GetAsync($"/api/staff/patients/{_patientId}/prescriptions")).StatusCode);
    }

    // ---------------- Prescription access ----------------

    [Fact]
    public async Task WrongPatientScopeCannotReadOrMutate()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ClinicDbContext>();
        var admin = scope.ServiceProvider.GetRequiredService<StaffAdministration>();
        var otherPatient = new Patient("Other Scope Patient", "other-scope-phone");
        db.Add(otherPatient);
        await db.SaveChangesAsync();
        // Same permissions but scoped only to the other patient.
        await admin.ChangeAsync(_doctorUserId, "set-grants",
            approvedRoles: ["Doctor"], permissions: DoctorPermissions, scopes: [_doctorEntityId], patientScopes: [otherPatient.Id]);

        using var client = await EnrolledClient(DoctorName);
        var visitId = await SeedVisitAsync();

        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.GetAsync($"/api/staff/patients/{_patientId}/prescriptions")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.PostAsJsonAsync($"/api/staff/patients/{_patientId}/visits/{visitId}/prescriptions", new { })).StatusCode);
        // The other patient's (empty) collection is reachable with its own scope.
        Assert.Equal(HttpStatusCode.OK,
            (await client.GetAsync($"/api/staff/patients/{otherPatient.Id}/prescriptions")).StatusCode);
    }

    [Fact]
    public async Task DoctorAssistantAndReceptionistCannotAccessPrescriptions()
    {
        using var assistant = await EnrolledClient(AssistantName);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await assistant.GetAsync($"/api/staff/patients/{_patientId}/prescriptions")).StatusCode);

        using var receptionist = await EnrolledClient(ReceptionistName);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await receptionist.GetAsync($"/api/staff/patients/{_patientId}/prescriptions")).StatusCode);
    }

    [Fact]
    public async Task DifferentAssociatedDoctorCannotCreateOrMutate()
    {
        using var doctor = await EnrolledClient(DoctorName);
        var visitId = await SeedVisitAsync();
        var draft = await CreateDraft(doctor, visitId);
        var prescriptionId = draft.GetProperty("id").GetGuid();

        // Same patient scope and permissions; scheduling claims even name the first doctor;
        // persisted AssociatedDoctorId still points at a different doctor.
        using var other = await EnrolledClient(OtherDoctorName);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await other.PostAsJsonAsync($"/api/staff/patients/{_patientId}/visits/{visitId}/prescriptions", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await other.PutAsJsonAsync($"/api/staff/patients/{_patientId}/prescriptions/{prescriptionId}/notes",
                new { Notes = "hijack", ExpectedRowVersion = draft.GetProperty("rowVersion").GetString() })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await other.PostAsJsonAsync($"/api/staff/patients/{_patientId}/prescriptions/{prescriptionId}/finalize",
                new { ExpectedRowVersion = draft.GetProperty("rowVersion").GetString() })).StatusCode);

        // Reading is permitted with scope + prescriptions.read (doctor authority gates mutation).
        Assert.Equal(HttpStatusCode.OK,
            (await other.GetAsync($"/api/staff/patients/{_patientId}/prescriptions/{prescriptionId}")).StatusCode);
    }

    [Fact]
    public async Task PersistedAssociatedDoctorIdChangeDecidesAuthorityForAnOldCookie()
    {
        // Transfer the prescription ownership: the second doctor's persisted association is
        // changed to the first doctor; the old cookie carries no association claim.
        using (var scope = _factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<StaffUser>>();
            var other = (await users.FindByIdAsync(_otherDoctorUserId))!;
            other.AssociatedDoctorId = _doctorEntityId;
            Assert.True((await users.UpdateAsync(other)).Succeeded);
        }

        using var client = await EnrolledClient(OtherDoctorName);
        var visitId = await SeedVisitAsync();
        var response = await client.PostAsJsonAsync($"/api/staff/patients/{_patientId}/visits/{visitId}/prescriptions", new { });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    // ---------------- Workflow ----------------

    [Fact]
    public async Task CreateDraftDerivesPatientAndDoctorFromTheVisit()
    {
        using var client = await EnrolledClient(DoctorName);
        var visitId = await SeedVisitAsync();
        var draft = await CreateDraft(client, visitId);

        Assert.Equal(visitId, draft.GetProperty("visitId").GetGuid());
        Assert.Equal(_patientId, draft.GetProperty("patientId").GetGuid());
        Assert.Equal(_doctorEntityId, draft.GetProperty("doctorId").GetGuid());
        Assert.Equal(0, draft.GetProperty("status").GetInt32());
        Assert.Equal(8, Convert.FromBase64String(draft.GetProperty("rowVersion").GetString()!).Length);
        Assert.True(draft.GetProperty("rowVersion").GetString() != Convert.ToBase64String(new byte[8]));
        Assert.Equal(0, draft.GetProperty("items").GetArrayLength());
        // Lifecycle attribution and account data stay out of responses.
        Assert.False(draft.TryGetProperty("createdByStaffId", out _));
        Assert.False(draft.TryGetProperty("passwordHash", out _));
    }

    [Fact]
    public async Task RoutePatientAndVisitMismatchReturn404()
    {
        using var client = await EnrolledClient(DoctorName);
        var visitId = await SeedVisitAsync();
        var draft = await CreateDraft(client, visitId);
        var prescriptionId = draft.GetProperty("id").GetGuid();

        // Visit route under a wrong patient hides existence — the doctor must hold scope for
        // both patients so the mismatch is a 404 and not a scope denial.
        Guid wrongPatientVisit;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ClinicDbContext>();
            var admin = scope.ServiceProvider.GetRequiredService<StaffAdministration>();
            var otherPatient = new Patient("Mismatch Patient", "mismatch-phone");
            db.Add(otherPatient);
            await db.SaveChangesAsync();
            wrongPatientVisit = otherPatient.Id;
            // set-grants rotates the security stamp, so a fresh session is required afterwards.
            await admin.ChangeAsync(_doctorUserId, "set-grants",
                approvedRoles: ["Doctor"], permissions: DoctorPermissions, scopes: [_doctorEntityId],
                patientScopes: [_patientId, otherPatient.Id]);
        }

        using var rescoped = await EnrolledClient(DoctorName);

        var mismatch = await rescoped.PostAsJsonAsync($"/api/staff/patients/{wrongPatientVisit}/visits/{visitId}/prescriptions", new { });
        Assert.Equal(HttpStatusCode.NotFound, mismatch.StatusCode);
        Assert.Equal("visit_not_found", (await Json(mismatch)).GetProperty("code").GetString());

        // Prescription route under a wrong patient hides existence.
        var cross = await rescoped.GetAsync($"/api/staff/patients/{wrongPatientVisit}/prescriptions/{prescriptionId}");
        Assert.Equal(HttpStatusCode.NotFound, cross.StatusCode);
        Assert.Equal("prescription_not_found", (await Json(cross)).GetProperty("code").GetString());

        // Unknown visit.
        var unknown = await rescoped.PostAsJsonAsync($"/api/staff/patients/{_patientId}/visits/{Guid.NewGuid()}/prescriptions", new { });
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task IncompleteDraftItemsFinalizeAndLifecycleViaHttp()
    {
        using var client = await EnrolledClient(DoctorName);
        var medicationId = await SeedMedicationAsync("HttpLifecycle");
        var visitId = await SeedVisitAsync();
        var draft = await CreateDraft(client, visitId);
        var prescriptionId = draft.GetProperty("id").GetGuid();

        // An incomplete item is accepted while Draft.
        var incomplete = await AddItem(client, draft, medicationId, dose: null, frequency: null, duration: null);
        var item = incomplete.GetProperty("items")[0];
        Assert.True(item.GetProperty("dose").ValueKind == JsonValueKind.Null);

        // Finalizing an incomplete draft conflicts.
        var incompleteFinalize = await client.PostAsJsonAsync(
            $"/api/staff/patients/{_patientId}/prescriptions/{prescriptionId}/finalize",
            new { ExpectedRowVersion = incomplete.GetProperty("rowVersion").GetString() });
        Assert.Equal(HttpStatusCode.Conflict, incompleteFinalize.StatusCode);
        Assert.Equal("incomplete_prescription", (await Json(incompleteFinalize)).GetProperty("code").GetString());

        // Completing the item, then finalizing, releasing and cancelling.
        var completed = await client.PutAsJsonAsync(
            $"/api/staff/patients/{_patientId}/prescriptions/{prescriptionId}/items/{item.GetProperty("id").GetGuid()}",
            new { ReselectedMedicationId = (Guid?)null, Dose = "1 tablet", Frequency = "twice daily",
                  Duration = "7 days", Instructions = (string?)null, DisplayOrder = (int?)null,
                  ExpectedRowVersion = incomplete.GetProperty("rowVersion").GetString() });
        Assert.Equal(HttpStatusCode.OK, completed.StatusCode);

        var finalizedResponse = await client.PostAsJsonAsync(
            $"/api/staff/patients/{_patientId}/prescriptions/{prescriptionId}/finalize",
            new { ExpectedRowVersion = (await Json(completed)).GetProperty("rowVersion").GetString() });
        Assert.Equal(HttpStatusCode.OK, finalizedResponse.StatusCode);
        var finalized = await Json(finalizedResponse);
        Assert.Equal(1, finalized.GetProperty("status").GetInt32());

        // Finalized items and notes are immutable.
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync(
            $"/api/staff/patients/{_patientId}/prescriptions/{prescriptionId}/notes",
            new { Notes = "late edit", ExpectedRowVersion = finalized.GetProperty("rowVersion").GetString() })).StatusCode);
        Assert.Equal("invalid_lifecycle",
            (await Json(await client.PostAsJsonAsync(
                $"/api/staff/patients/{_patientId}/prescriptions/{prescriptionId}/items",
                new { MedicationId = medicationId, Dose = "2", Frequency = "daily", Duration = "1 day",
                      ExpectedRowVersion = finalized.GetProperty("rowVersion").GetString() })))
            .GetProperty("code").GetString());

        // Release is a separate step.
        var releasedResponse = await client.PostAsJsonAsync(
            $"/api/staff/patients/{_patientId}/prescriptions/{prescriptionId}/release",
            new { ExpectedRowVersion = finalized.GetProperty("rowVersion").GetString() });
        Assert.Equal(HttpStatusCode.OK, releasedResponse.StatusCode);
        var released = await Json(releasedResponse);
        Assert.Equal(2, released.GetProperty("status").GetInt32());
        Assert.NotEqual(JsonValueKind.Null, released.GetProperty("releasedAtUtc").ValueKind);

        // Cancel preserves release history and requires a reason.
        var noReason = await client.PostAsJsonAsync(
            $"/api/staff/patients/{_patientId}/prescriptions/{prescriptionId}/cancel",
            new { Reason = "", ExpectedRowVersion = released.GetProperty("rowVersion").GetString() });
        Assert.Equal(HttpStatusCode.BadRequest, noReason.StatusCode);

        var cancelledResponse = await client.PostAsJsonAsync(
            $"/api/staff/patients/{_patientId}/prescriptions/{prescriptionId}/cancel",
            new { Reason = "dispensed wrong strength", ExpectedRowVersion = released.GetProperty("rowVersion").GetString() });
        Assert.Equal(HttpStatusCode.OK, cancelledResponse.StatusCode);
        var cancelled = await Json(cancelledResponse);
        Assert.Equal(3, cancelled.GetProperty("status").GetInt32());
        Assert.Equal("dispensed wrong strength", cancelled.GetProperty("cancellationReason").GetString());
        Assert.NotEqual(JsonValueKind.Null, cancelled.GetProperty("releasedAtUtc").ValueKind);

        // Release after cancellation is refused.
        var releaseAfterCancel = await client.PostAsJsonAsync(
            $"/api/staff/patients/{_patientId}/prescriptions/{prescriptionId}/release",
            new { ExpectedRowVersion = cancelled.GetProperty("rowVersion").GetString() });
        Assert.Equal(HttpStatusCode.Conflict, releaseAfterCancel.StatusCode);
        Assert.Equal("invalid_lifecycle", (await Json(releaseAfterCancel)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task FinalizingWithInactiveMedicationReturns409()
    {
        using var client = await EnrolledClient(DoctorName);
        var medicationId = await SeedMedicationAsync("HttpInactive");
        byte[] medicationVersion;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ClinicDbContext>();
            medicationVersion = (await db.Medications.SingleAsync(x => x.Id == medicationId)).RowVersion.ToArray();
        }
        var visitId = await SeedVisitAsync();
        var draft = await CreateDraft(client, visitId);
        draft = await AddItem(client, draft, medicationId);

        var deactivate = await client.PostAsJsonAsync($"/api/staff/medications/{medicationId}/deactivate",
            new { ExpectedRowVersion = Convert.ToBase64String(medicationVersion) });
        Assert.Equal(HttpStatusCode.OK, deactivate.StatusCode);

        var finalize = await client.PostAsJsonAsync(
            $"/api/staff/patients/{_patientId}/prescriptions/{draft.GetProperty("id").GetGuid()}/finalize",
            new { ExpectedRowVersion = draft.GetProperty("rowVersion").GetString() });
        Assert.Equal(HttpStatusCode.Conflict, finalize.StatusCode);
        Assert.Equal("inactive_medication", (await Json(finalize)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task ReorderAndRemoveItemRoutesWorkThroughHttp()
    {
        using var client = await EnrolledClient(DoctorName);
        var medicationId = await SeedMedicationAsync("HttpOrder");
        var visitId = await SeedVisitAsync();
        var draft = await CreateDraft(client, visitId);
        var prescriptionId = draft.GetProperty("id").GetGuid();
        draft = await AddItem(client, draft, medicationId, "1 tablet");
        draft = await AddItem(client, draft, medicationId, "2 tablets");
        var first = draft.GetProperty("items")[0].GetProperty("id").GetGuid();
        var second = draft.GetProperty("items")[1].GetProperty("id").GetGuid();

        // items/order must not be captured by the items/{itemId:guid} route template.
        var reorder = await client.PutAsJsonAsync(
            $"/api/staff/patients/{_patientId}/prescriptions/{prescriptionId}/items/order",
            new { OrderedItemIds = new[] { second, first }, ExpectedRowVersion = draft.GetProperty("rowVersion").GetString() });
        Assert.Equal(HttpStatusCode.OK, reorder.StatusCode);
        var reordered = await Json(reorder);
        Assert.Equal(second, reordered.GetProperty("items")[0].GetProperty("id").GetGuid());
        Assert.Equal(first, reordered.GetProperty("items")[1].GetProperty("id").GetGuid());

        // DELETE with a JSON body carries the expected root rowversion.
        var remove = new HttpRequestMessage(HttpMethod.Delete,
            $"/api/staff/patients/{_patientId}/prescriptions/{prescriptionId}/items/{second}")
        {
            Content = JsonContent.Create(new { ExpectedRowVersion = reordered.GetProperty("rowVersion").GetString() })
        };
        var removed = await client.SendAsync(remove);
        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        Assert.Equal(1, (await Json(removed)).GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task CatalogSnapshotDoesNotChangeAfterCatalogUpdate()
    {
        using var client = await EnrolledClient(DoctorName);
        var medicationId = await SeedMedicationAsync("HttpSnapshot");
        var visitId = await SeedVisitAsync();
        var draft = await CreateDraft(client, visitId);
        draft = await AddItem(client, draft, medicationId);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ClinicDbContext>();
            var medication = await db.Medications.SingleAsync(x => x.Id == medicationId);
            medication.UpdateDetails(medication.GenericNameEn, null, "New-Brand", null, "800", "mg",
                DosageForm.Capsule, MedicationRoute.Oral, null, "admin", DateTimeOffset.UtcNow);
            await db.SaveChangesAsync();
        }

        var current = await client.GetAsync($"/api/staff/patients/{_patientId}/prescriptions/{draft.GetProperty("id").GetGuid()}");
        var item = (await Json(current)).GetProperty("items")[0];
        Assert.Equal("500", item.GetProperty("strength").GetString());
        Assert.Equal((int)DosageForm.Tablet, item.GetProperty("form").GetInt32());
        Assert.Equal("HttpSnapshot", item.GetProperty("genericNameEn").GetString());
    }

    [Fact]
    public async Task StaleRootVersionReturns409OnChildMutation()
    {
        using var client = await EnrolledClient(DoctorName);
        var medicationId = await SeedMedicationAsync("HttpStale");
        var visitId = await SeedVisitAsync();
        var draft = await CreateDraft(client, visitId);
        var staleVersion = draft.GetProperty("rowVersion").GetString()!;

        var updated = await AddItem(client, draft, medicationId);

        // Child mutation returned the updated root version, distinct from the create version.
        Assert.NotEqual(staleVersion, updated.GetProperty("rowVersion").GetString());

        var stale = await client.PostAsJsonAsync(
            $"/api/staff/patients/{_patientId}/prescriptions/{draft.GetProperty("id").GetGuid()}/items",
            new { MedicationId = medicationId, Dose = "1", Frequency = "daily", Duration = "1 day",
                  ExpectedRowVersion = staleVersion });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("prescription_changed", (await Json(stale)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task ReplacementLifecycleViaHttp()
    {
        using var client = await EnrolledClient(DoctorName);
        var medicationId = await SeedMedicationAsync("HttpReplacement");
        var visitId = await SeedVisitAsync();
        var draft = await CreateDraft(client, visitId);
        draft = await AddItem(client, draft, medicationId);

        var finalized = await client.PostAsJsonAsync(
            $"/api/staff/patients/{_patientId}/prescriptions/{draft.GetProperty("id").GetGuid()}/finalize",
            new { ExpectedRowVersion = draft.GetProperty("rowVersion").GetString() });
        Assert.Equal(HttpStatusCode.OK, finalized.StatusCode);
        var original = await Json(finalized);

        var cancelled = await client.PostAsJsonAsync(
            $"/api/staff/patients/{_patientId}/prescriptions/{original.GetProperty("id").GetGuid()}/cancel",
            new { Reason = "wrong drug", ExpectedRowVersion = original.GetProperty("rowVersion").GetString() });
        Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);
        var originalId = original.GetProperty("id").GetGuid();

        var stale = await client.PostAsJsonAsync(
            $"/api/staff/patients/{_patientId}/prescriptions/{originalId}/replacement",
            new { Notes = (string?)null, ExpectedOriginalRowVersion = original.GetProperty("rowVersion").GetString() });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("prescription_changed", (await Json(stale)).GetProperty("code").GetString());

        var replacement = await client.PostAsJsonAsync(
            $"/api/staff/patients/{_patientId}/prescriptions/{originalId}/replacement",
            new { Notes = (string?)"corrected", ExpectedOriginalRowVersion = (await Json(cancelled)).GetProperty("rowVersion").GetString() });
        Assert.Equal(HttpStatusCode.Created, replacement.StatusCode);
        var replacementDetails = await Json(replacement);
        Assert.NotEqual(originalId, replacementDetails.GetProperty("id").GetGuid());
        Assert.Equal(0, replacementDetails.GetProperty("status").GetInt32());
        Assert.Equal(originalId, replacementDetails.GetProperty("replacesPrescriptionId").GetGuid());

        // The original records the replacement back-link.
        var originalAfter = await client.GetAsync($"/api/staff/patients/{_patientId}/prescriptions/{originalId}");
        Assert.Equal(replacementDetails.GetProperty("id").GetGuid(),
            (await Json(originalAfter)).GetProperty("replacedByPrescriptionId").GetGuid());

        // Exactly one replacement: the second attempt is a controlled 409.
        var second = await client.PostAsJsonAsync(
            $"/api/staff/patients/{_patientId}/prescriptions/{originalId}/replacement",
            new { Notes = (string?)null, ExpectedOriginalRowVersion = (await Json(originalAfter)).GetProperty("rowVersion").GetString() });
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Contains((await Json(second)).GetProperty("code").GetString(),
            new[] { "replacement_mismatch", "prescription_changed" });
    }

    [Fact]
    public async Task PrescriptionResponsesUseNoStoreAndDoNotLeakAccountData()
    {
        using var client = await EnrolledClient(DoctorName);
        var visitId = await SeedVisitAsync();
        var draft = await CreateDraft(client, visitId);

        var list = await client.GetAsync($"/api/staff/patients/{_patientId}/visits/{visitId}/prescriptions");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.True(list.Headers.CacheControl?.NoStore);

        var details = await client.GetAsync($"/api/staff/patients/{_patientId}/prescriptions/{draft.GetProperty("id").GetGuid()}");
        Assert.Equal(HttpStatusCode.OK, details.StatusCode);
        var body = await details.Content.ReadAsStringAsync();
        Assert.DoesNotContain("password", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("createdByStaffId", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("finalizedByStaffId", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("patient_record_id", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("associatedDoctorId", body, StringComparison.OrdinalIgnoreCase);
    }
}
