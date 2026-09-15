using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Clinic.Api.Visits;
using Clinic.Application.Visits;
using Clinic.Domain.Entities;
using Clinic.Domain.Enums;
using Clinic.Infrastructure.Authentication;
using Clinic.Infrastructure.Persistence;
using Clinic.IntegrationTests.Api;
using Clinic.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Clinic.IntegrationTests.Visits;

// Real HTTP tests for Visit + Vital Measurements endpoints with authentication, authorization, CSRF, and concurrency.
public sealed class StaffVisitsHttpTests : IAsyncLifetime
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

    private const string Password = "Synthetic-Visit-Password-99!";
    private const string DoctorName = "synthetic-visit-doctor";
    private const string AssistantName = "synthetic-visit-assistant";
    private const string ReceptionistName = "synthetic-visit-receptionist";
    private readonly SqlDatabaseFixture _database = new();
    private readonly PolicyTestClock _clock = new() { Now = DateTimeOffset.UtcNow };
    private readonly CaptureLogs _logs = new();
    private WebApplicationFactory<Program> _factory = null!;
    private Guid _doctorEntityId;
    private Guid _patientId;
    private string _doctorUserId = null!;
    private string _assistantUserId = null!;
    private string _receptionistUserId = null!;

    public async Task InitializeAsync()
    {
        await _database.InitializeAsync();
        _factory = new BookingApiFactory(_database.ConnectionString).WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<TimeProvider>(_clock);
                services.AddSingleton<ILoggerProvider>(_logs);
            }));

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ClinicDbContext>();
        var admin = scope.ServiceProvider.GetRequiredService<StaffAdministration>();
        var users = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<StaffUser>>();

        var doctor = new Doctor("Synthetic Visit Doctor");
        var patient = new Patient("Synthetic Visit Patient", "shared-phone");
        db.Add(doctor);
        db.Add(patient);
        await db.SaveChangesAsync();
        _doctorEntityId = doctor.Id;
        _patientId = patient.Id;

        _doctorUserId = await admin.ProvisionFirstDoctorAsync(DoctorName, Password, _doctorEntityId, [_doctorEntityId]);
        await admin.ChangeAsync(_doctorUserId, "set-grants",
            approvedRoles: ["Doctor"],
            permissions: ["visits.read", "visits.write", "visits.finalize", "visits.amend", "vitals.write"],
            scopes: [_doctorEntityId],
            patientScopes: [_patientId]);

        var assistant = new StaffUser { UserName = AssistantName, LockoutEnabled = true };
        var assistantResult = await users.CreateAsync(assistant, Password);
        if (!assistantResult.Succeeded) throw new InvalidOperationException("Failed to create assistant user");
        _assistantUserId = assistant.Id;
        await admin.ChangeAsync(_assistantUserId, "set-grants",
            approvedRoles: ["DoctorAssistant"],
            permissions: ["visits.read", "vitals.write"],
            scopes: null,
            patientScopes: [_patientId]);

        var receptionist = new StaffUser { UserName = ReceptionistName, LockoutEnabled = true };
        var receptionistResult = await users.CreateAsync(receptionist, Password);
        if (!receptionistResult.Succeeded) throw new InvalidOperationException("Failed to create receptionist user");
        _receptionistUserId = receptionist.Id;
        await admin.ChangeAsync(_receptionistUserId, "set-grants",
            approvedRoles: ["Receptionist"],
            permissions: [],
            scopes: null,
            patientScopes: []);
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

    private async Task<HttpResponseMessage> Login(HttpClient client, string userName, string password = Password)
    {
        await Csrf(client);
        var response = await client.PostAsJsonAsync("/api/staff/auth/login", new { userName, password });
        if (response.IsSuccessStatusCode)
            Token(client, (await Json(response)).GetProperty("csrfToken").GetString()!);
        return response;
    }

    private async Task Enroll(HttpClient client, string userName)
    {
        using var login = await Login(client, userName);
        Assert.True(login.IsSuccessStatusCode);
        using var setup = await client.PostAsync("/api/staff/auth/enrollment/setup", null);
        Assert.Equal(HttpStatusCode.OK, setup.StatusCode);
        var key = (await Json(setup)).GetProperty("sharedKey").GetString()!;
        using var verify = await client.PostAsJsonAsync("/api/staff/auth/enrollment/verify", new { code = Totp(key) });
        Assert.Equal(HttpStatusCode.OK, verify.StatusCode);
        Token(client, (await Json(verify)).GetProperty("csrfToken").GetString()!);
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
            if (bits >= 8)
            {
                bits -= 8;
                bytes.Add((byte)(buffer >> bits));
            }
        }
        var counter = BitConverter.GetBytes(IPAddress.HostToNetworkOrder(DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30));
        var hash = System.Security.Cryptography.HMACSHA1.HashData(bytes.ToArray(), counter);
        var offset = hash[^1] & 15;
        var number = ((hash[offset] & 127) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (number % 1000000).ToString("D6");
    }

    [Theory]
    [InlineData("Doctor", true, true, 201)]
    [InlineData("Doctor", false, true, 403)]
    [InlineData("Doctor", true, false, 403)]
    [InlineData("DoctorAssistant", true, true, 403)]
    [InlineData("Receptionist", true, true, 403)]
    public async Task CreateVisitRequiresExactRolePermissionAndPatientScope(string role, bool permission, bool scope, int expected)
    {
        var userName = role == "Doctor" ? DoctorName : role == "DoctorAssistant" ? AssistantName : ReceptionistName;
        var userId = role == "Doctor" ? _doctorUserId : role == "DoctorAssistant" ? _assistantUserId : _receptionistUserId;

        if (!permission || !scope)
        {
            using var adminScope = _factory.Services.CreateScope();
            var admin = adminScope.ServiceProvider.GetRequiredService<StaffAdministration>();
            await admin.ChangeAsync(userId, "set-grants",
                approvedRoles: [role],
                permissions: permission ? ["visits.read", "visits.write", "visits.finalize", "visits.amend", "vitals.write"] : [],
                scopes: role == "Doctor" ? [_doctorEntityId] : [],
                patientScopes: scope ? [_patientId] : []);
        }

        using var client = Client();
        await Enroll(client, userName);

        var body = new { DoctorId = _doctorEntityId, OccurredAtUtc = _clock.Now };
        var response = await client.PostAsJsonAsync($"/api/staff/patients/{_patientId}/visits", body);
        Assert.Equal(expected, (int)response.StatusCode);
        if (response.IsSuccessStatusCode)
            Assert.True(response.Headers.CacheControl?.NoStore);
    }

    [Fact]
    public async Task DoctorCannotCreateVisitForAnotherDoctorWithoutAuthority()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ClinicDbContext>();
        var otherDoctor = new Doctor("Other Synthetic Doctor");
        db.Add(otherDoctor);
        await db.SaveChangesAsync();

        using var client = Client();
        await Enroll(client, DoctorName);

        var body = new { DoctorId = otherDoctor.Id, OccurredAtUtc = _clock.Now };
        var response = await client.PostAsJsonAsync($"/api/staff/patients/{_patientId}/visits", body);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task CreateVisitRequiresCsrf()
    {
        using var client = Client();
        await Enroll(client, DoctorName);
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");

        var body = new { DoctorId = _doctorEntityId, OccurredAtUtc = _clock.Now };
        var response = await client.PostAsJsonAsync($"/api/staff/patients/{_patientId}/visits", body);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_csrf_token", (await Json(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task CreateVisitRejectsInvalidInput()
    {
        using var client = Client();
        await Enroll(client, DoctorName);

        var missingDoctor = new { OccurredAtUtc = _clock.Now };
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsJsonAsync($"/api/staff/patients/{_patientId}/visits", missingDoctor)).StatusCode);

        var emptyDoctor = new { DoctorId = Guid.Empty, OccurredAtUtc = _clock.Now };
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsJsonAsync($"/api/staff/patients/{_patientId}/visits", emptyDoctor)).StatusCode);
    }

    [Theory]
    [InlineData("Doctor", 200)]
    [InlineData("DoctorAssistant", 200)]
    [InlineData("Receptionist", 403)]
    public async Task ListVisitsRequiresRoleAndPermission(string role, int expected)
    {
        var userName = role == "Doctor" ? DoctorName : role == "DoctorAssistant" ? AssistantName : ReceptionistName;
        using var client = Client();
        await Enroll(client, userName);

        var response = await client.GetAsync($"/api/staff/patients/{_patientId}/visits");
        Assert.Equal(expected, (int)response.StatusCode);
        if (response.IsSuccessStatusCode)
            Assert.True(response.Headers.CacheControl?.NoStore);
    }

    [Theory]
    [InlineData("scope")]
    [InlineData("permission")]
    [InlineData("role")]
    public async Task RemovedPersistedGrantInvalidatesCookieWithoutStampChange(string grant)
    {
        using var client = Client();
        await Enroll(client, DoctorName);

        // Create a visit first
        var createBody = new { DoctorId = _doctorEntityId, OccurredAtUtc = _clock.Now };
        var createResponse = await client.PostAsJsonAsync($"/api/staff/patients/{_patientId}/visits", createBody);
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var visitId = (await Json(createResponse)).GetProperty("id").GetGuid();

        // Verify can access
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/staff/patients/{_patientId}/visits/{visitId}")).StatusCode);

        // Remove grant without changing security stamp
        using (var scope = _factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<StaffUser>>();
            var user = (await users.FindByIdAsync(_doctorUserId))!;
            var stamp = user.SecurityStamp;

            if (grant == "role")
            {
                Assert.True((await users.AddToRoleAsync(user, "DoctorAssistant")).Succeeded);
                Assert.True((await users.RemoveFromRoleAsync(user, "Doctor")).Succeeded);
            }
            else if (grant == "scope")
            {
                Assert.True((await users.RemoveClaimAsync(user,
                    new System.Security.Claims.Claim("patient_record_id", _patientId.ToString()))).Succeeded);
            }
            else // permission
            {
                Assert.True((await users.RemoveClaimAsync(user,
                    new System.Security.Claims.Claim("permission", "visits.read"))).Succeeded);
            }

            Assert.Equal(stamp, user.SecurityStamp);
        }

        // Next request must be unauthorized
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.GetAsync($"/api/staff/patients/{_patientId}/visits/{visitId}")).StatusCode);
    }

    [Fact]
    public async Task DoctorAssistantCannotFinalizeVisit()
    {
        // Doctor creates a visit
        using var doctorClient = Client();
        await Enroll(doctorClient, DoctorName);
        var createBody = new { DoctorId = _doctorEntityId, OccurredAtUtc = _clock.Now };
        var createResponse = await doctorClient.PostAsJsonAsync($"/api/staff/patients/{_patientId}/visits", createBody);
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var visit = await Json(createResponse);
        var visitId = visit.GetProperty("id").GetGuid();
        var rowVersion = visit.GetProperty("rowVersion").GetString()!;

        // DoctorAssistant tries to finalize
        using var assistantClient = Client();
        await Enroll(assistantClient, AssistantName);
        var finalizeBody = new { ExpectedRowVersion = Convert.FromBase64String(rowVersion) };
        var response = await assistantClient.PostAsJsonAsync($"/api/staff/patients/{_patientId}/visits/{visitId}/finalize", finalizeBody);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task DoctorAssistantCannotAmendVisit()
    {
        // Doctor creates and finalizes a visit
        using var doctorClient = Client();
        await Enroll(doctorClient, DoctorName);
        var createBody = new { DoctorId = _doctorEntityId, OccurredAtUtc = _clock.Now };
        var createResponse = await doctorClient.PostAsJsonAsync($"/api/staff/patients/{_patientId}/visits", createBody);
        var visit = await Json(createResponse);
        var visitId = visit.GetProperty("id").GetGuid();
        var rowVersion = visit.GetProperty("rowVersion").GetString()!;

        var finalizeResponse = await doctorClient.PostAsJsonAsync($"/api/staff/patients/{_patientId}/visits/{visitId}/finalize",
            new { ExpectedRowVersion = Convert.FromBase64String(rowVersion) });
        var finalized = await Json(finalizeResponse);
        rowVersion = finalized.GetProperty("rowVersion").GetString()!;

        // DoctorAssistant tries to amend
        using var assistantClient = Client();
        await Enroll(assistantClient, AssistantName);
        var amendBody = new { Reason = "Test", AmendmentText = "Test amendment", ExpectedRowVersion = Convert.FromBase64String(rowVersion) };
        var response = await assistantClient.PostAsJsonAsync($"/api/staff/patients/{_patientId}/visits/{visitId}/amendments", amendBody);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task DoctorAssistantCanRecordVitals()
    {
        // Doctor creates a visit
        using var doctorClient = Client();
        await Enroll(doctorClient, DoctorName);
        var createBody = new { DoctorId = _doctorEntityId, OccurredAtUtc = _clock.Now };
        var createResponse = await doctorClient.PostAsJsonAsync($"/api/staff/patients/{_patientId}/visits", createBody);
        var visit = await Json(createResponse);
        var visitId = visit.GetProperty("id").GetGuid();
        var rowVersion = visit.GetProperty("rowVersion").GetString()!;

        // DoctorAssistant records vitals
        using var assistantClient = Client();
        await Enroll(assistantClient, AssistantName);
        var vitalBody = new AddVitalBody(
            new VitalReadingBody.BloodPressureBody(120, 80),
            _clock.Now,
            Convert.FromBase64String(rowVersion));
        var response = await assistantClient.PostAsJsonAsync($"/api/staff/patients/{_patientId}/visits/{visitId}/vitals", vitalBody);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ConcurrentDraftUpdateConflictReturns409()
    {
        using var client = Client();
        await Enroll(client, DoctorName);

        var createBody = new { DoctorId = _doctorEntityId, OccurredAtUtc = _clock.Now };
        var createResponse = await client.PostAsJsonAsync($"/api/staff/patients/{_patientId}/visits", createBody);
        var visit = await Json(createResponse);
        var visitId = visit.GetProperty("id").GetGuid();
        var rowVersion = Convert.FromBase64String(visit.GetProperty("rowVersion").GetString()!);

        var updateBody = new { ChiefComplaint = "Headache", ExpectedRowVersion = rowVersion };
        var firstUpdate = await client.PutAsJsonAsync($"/api/staff/patients/{_patientId}/visits/{visitId}", updateBody);
        Assert.Equal(HttpStatusCode.OK, firstUpdate.StatusCode);

        // Stale update with old rowVersion
        var staleUpdate = await client.PutAsJsonAsync($"/api/staff/patients/{_patientId}/visits/{visitId}", updateBody);
        Assert.Equal(HttpStatusCode.Conflict, staleUpdate.StatusCode);
        Assert.Equal("visit_changed", (await Json(staleUpdate)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task CrossPatientVisitIdProbingReturns404WithoutLeakage()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ClinicDbContext>();
        var otherPatient = new Patient("Other Patient", "other-phone");
        db.Add(otherPatient);
        await db.SaveChangesAsync();

        var admin = scope.ServiceProvider.GetRequiredService<StaffAdministration>();
        await admin.ChangeAsync(_doctorUserId, "set-grants",
            approvedRoles: ["Doctor"],
            permissions: ["visits.read", "visits.write", "visits.finalize", "visits.amend", "vitals.write"],
            scopes: [_doctorEntityId],
            patientScopes: [_patientId, otherPatient.Id]);

        using var client = Client();
        await Enroll(client, DoctorName);

        var createBody = new { DoctorId = _doctorEntityId, OccurredAtUtc = _clock.Now };
        var createResponse = await client.PostAsJsonAsync($"/api/staff/patients/{_patientId}/visits", createBody);
        var visitId = (await Json(createResponse)).GetProperty("id").GetGuid();

        // Try to access via wrong patient route even when authorized for both patients
        var response = await client.GetAsync($"/api/staff/patients/{otherPatient.Id}/visits/{visitId}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("visit_not_found", (await Json(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task PrivacyProtectionKeepsClinicalContentOutOfProblemDetails()
    {
        using var client = Client();
        await Enroll(client, DoctorName);

        var createBody = new { DoctorId = _doctorEntityId, OccurredAtUtc = _clock.Now };
        var createResponse = await client.PostAsJsonAsync($"/api/staff/patients/{_patientId}/visits", createBody);
        var visit = await Json(createResponse);
        var visitId = visit.GetProperty("id").GetGuid();
        var rowVersion = Convert.FromBase64String(visit.GetProperty("rowVersion").GetString()!);

        var sensitiveContent = "SENSITIVE-DIAGNOSIS-" + Guid.NewGuid();
        var updateBody = new { Diagnosis = sensitiveContent, ExpectedRowVersion = rowVersion };
        var updateResponse = await client.PutAsJsonAsync($"/api/staff/patients/{_patientId}/visits/{visitId}", updateBody);
        Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);

        // Trigger a conflict error
        var staleUpdate = await client.PutAsJsonAsync($"/api/staff/patients/{_patientId}/visits/{visitId}", updateBody);
        Assert.Equal(HttpStatusCode.Conflict, staleUpdate.StatusCode);
        var errorBody = await staleUpdate.Content.ReadAsStringAsync();
        Assert.DoesNotContain(sensitiveContent, errorBody);
        Assert.DoesNotContain(sensitiveContent, _logs.Messages);
    }

    [Fact]
    public async Task IntermediateSessionCannotAccessVisits()
    {
        using var client = Client();
        await Csrf(client);
        var loginResponse = await client.PostAsJsonAsync("/api/staff/auth/login", new { userName = DoctorName, password = Password });
        Assert.True(loginResponse.IsSuccessStatusCode);
        Token(client, (await Json(loginResponse)).GetProperty("csrfToken").GetString()!);

        // Still in intermediate session (not completed TOTP)
        var response = await client.GetAsync($"/api/staff/patients/{_patientId}/visits");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ReceptionistCannotAccessAnyVisitData()
    {
        using var client = Client();
        await Enroll(client, ReceptionistName);

        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.GetAsync($"/api/staff/patients/{_patientId}/visits")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.GetAsync($"/api/staff/patients/{_patientId}/visits/{Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task ConcurrentFinalizeAndStaleEditReturns409()
    {
        using var client = Client();
        await Enroll(client, DoctorName);

        var createBody = new { DoctorId = _doctorEntityId, OccurredAtUtc = _clock.Now };
        var createResponse = await client.PostAsJsonAsync($"/api/staff/patients/{_patientId}/visits", createBody);
        var visit = await Json(createResponse);
        var visitId = visit.GetProperty("id").GetGuid();
        var rowVersion = Convert.FromBase64String(visit.GetProperty("rowVersion").GetString()!);

        // Finalize succeeds
        var finalizeResponse = await client.PostAsJsonAsync($"/api/staff/patients/{_patientId}/visits/{visitId}/finalize",
            new { ExpectedRowVersion = rowVersion });
        Assert.Equal(HttpStatusCode.OK, finalizeResponse.StatusCode);

        // Stale edit with old draft rowVersion returns 409
        var staleUpdateBody = new { ChiefComplaint = "Stale edit", ExpectedRowVersion = rowVersion };
        var staleUpdate = await client.PutAsJsonAsync($"/api/staff/patients/{_patientId}/visits/{visitId}", staleUpdateBody);
        Assert.Equal(HttpStatusCode.Conflict, staleUpdate.StatusCode);
        Assert.Equal("visit_changed", (await Json(staleUpdate)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task ConcurrentFinalizationAttemptsOnlyOneSucceeds()
    {
        using var client = Client();
        await Enroll(client, DoctorName);

        var createBody = new { DoctorId = _doctorEntityId, OccurredAtUtc = _clock.Now };
        var createResponse = await client.PostAsJsonAsync($"/api/staff/patients/{_patientId}/visits", createBody);
        var visit = await Json(createResponse);
        var visitId = visit.GetProperty("id").GetGuid();
        var rowVersion = Convert.FromBase64String(visit.GetProperty("rowVersion").GetString()!);

        // First finalize succeeds
        var finalizeBody = new { ExpectedRowVersion = rowVersion };
        var firstFinalize = await client.PostAsJsonAsync($"/api/staff/patients/{_patientId}/visits/{visitId}/finalize", finalizeBody);
        Assert.Equal(HttpStatusCode.OK, firstFinalize.StatusCode);

        // Second finalize with same rowVersion returns 409
        var secondFinalize = await client.PostAsJsonAsync($"/api/staff/patients/{_patientId}/visits/{visitId}/finalize", finalizeBody);
        Assert.Equal(HttpStatusCode.Conflict, secondFinalize.StatusCode);
        Assert.Equal("visit_changed", (await Json(secondFinalize)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task StaleVitalMutationReturns409()
    {
        using var client = Client();
        await Enroll(client, DoctorName);

        var createBody = new { DoctorId = _doctorEntityId, OccurredAtUtc = _clock.Now };
        var createResponse = await client.PostAsJsonAsync($"/api/staff/patients/{_patientId}/visits", createBody);
        var visit = await Json(createResponse);
        var visitId = visit.GetProperty("id").GetGuid();
        var rowVersion = Convert.FromBase64String(visit.GetProperty("rowVersion").GetString()!);

        // First vital succeeds
        var vitalBody1 = new AddVitalBody(new VitalReadingBody.HeartRateBody(72), _clock.Now, rowVersion);
        var firstVital = await client.PostAsJsonAsync($"/api/staff/patients/{_patientId}/visits/{visitId}/vitals", vitalBody1);
        Assert.Equal(HttpStatusCode.OK, firstVital.StatusCode);

        // Second vital with stale rowVersion returns 409
        var vitalBody2 = new AddVitalBody(new VitalReadingBody.TemperatureBody(37.0m), _clock.Now, rowVersion);
        var secondVital = await client.PostAsJsonAsync($"/api/staff/patients/{_patientId}/visits/{visitId}/vitals", vitalBody2);
        Assert.Equal(HttpStatusCode.Conflict, secondVital.StatusCode);
        Assert.Equal("visit_changed", (await Json(secondVital)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task StaleAmendmentMutationReturns409()
    {
        using var client = Client();
        await Enroll(client, DoctorName);

        // Create and finalize visit
        var createBody = new { DoctorId = _doctorEntityId, OccurredAtUtc = _clock.Now };
        var createResponse = await client.PostAsJsonAsync($"/api/staff/patients/{_patientId}/visits", createBody);
        var visit = await Json(createResponse);
        var visitId = visit.GetProperty("id").GetGuid();
        var rowVersion = Convert.FromBase64String(visit.GetProperty("rowVersion").GetString()!);

        var finalizeResponse = await client.PostAsJsonAsync($"/api/staff/patients/{_patientId}/visits/{visitId}/finalize",
            new { ExpectedRowVersion = rowVersion });
        var finalized = await Json(finalizeResponse);
        rowVersion = Convert.FromBase64String(finalized.GetProperty("rowVersion").GetString()!);

        // First amendment succeeds
        var amendBody1 = new { Reason = "Correction 1", AmendmentText = "Corrected diagnosis", ExpectedRowVersion = rowVersion };
        var firstAmend = await client.PostAsJsonAsync($"/api/staff/patients/{_patientId}/visits/{visitId}/amendments", amendBody1);
        Assert.Equal(HttpStatusCode.OK, firstAmend.StatusCode);

        // Second amendment with stale rowVersion returns 409
        var amendBody2 = new { Reason = "Correction 2", AmendmentText = "Additional correction", ExpectedRowVersion = rowVersion };
        var secondAmend = await client.PostAsJsonAsync($"/api/staff/patients/{_patientId}/visits/{visitId}/amendments", amendBody2);
        Assert.Equal(HttpStatusCode.Conflict, secondAmend.StatusCode);
        Assert.Equal("visit_changed", (await Json(secondAmend)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task RespiratoryRateRecordingAndRetrievalViaApi()
    {
        using var client = Client();
        await Enroll(client, DoctorName);

        var createBody = new { DoctorId = _doctorEntityId, OccurredAtUtc = _clock.Now };
        var createResponse = await client.PostAsJsonAsync($"/api/staff/patients/{_patientId}/visits", createBody);
        var visit = await Json(createResponse);
        var visitId = visit.GetProperty("id").GetGuid();
        var rowVersion = Convert.FromBase64String(visit.GetProperty("rowVersion").GetString()!);

        var vitalBody = new AddVitalBody(new VitalReadingBody.RespiratoryRateBody(18), _clock.Now, rowVersion);
        var vitalResponse = await client.PostAsJsonAsync($"/api/staff/patients/{_patientId}/visits/{visitId}/vitals", vitalBody);
        Assert.Equal(HttpStatusCode.OK, vitalResponse.StatusCode);

        var result = await Json(vitalResponse);
        var vitals = result.GetProperty("vitalMeasurements");
        Assert.Equal(1, vitals.GetArrayLength());
        var vital = vitals[0];
        Assert.Equal(6, vital.GetProperty("type").GetInt32());
        Assert.Equal(6, vital.GetProperty("unit").GetInt32());
        Assert.Equal(18, vital.GetProperty("respiratoryRateBreathsPerMin").GetInt32());
    }

    [Fact]
    public async Task FailedSaveDoesNotLeakPartialEntities()
    {
        using var client = Client();
        await Enroll(client, DoctorName);

        var createBody = new { DoctorId = _doctorEntityId, OccurredAtUtc = _clock.Now };
        var createResponse = await client.PostAsJsonAsync($"/api/staff/patients/{_patientId}/visits", createBody);
        var visit = await Json(createResponse);
        var visitId = visit.GetProperty("id").GetGuid();
        var rowVersion = Convert.FromBase64String(visit.GetProperty("rowVersion").GetString()!);

        // Trigger concurrent modification
        var updateBody1 = new { Diagnosis = "First diagnosis", ExpectedRowVersion = rowVersion };
        var firstUpdate = await client.PutAsJsonAsync($"/api/staff/patients/{_patientId}/visits/{visitId}", updateBody1);
        Assert.Equal(HttpStatusCode.OK, firstUpdate.StatusCode);

        // Stale update should fail cleanly without leaking tracked entities
        var staleUpdate = await client.PutAsJsonAsync($"/api/staff/patients/{_patientId}/visits/{visitId}", updateBody1);
        Assert.Equal(HttpStatusCode.Conflict, staleUpdate.StatusCode);

        // Verify database state consistency: get visit with fresh context
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ClinicDbContext>();
        var visitFromDb = await db.Set<Visit>()
            .Include(v => v.VitalMeasurements)
            .Include(v => v.Amendments)
            .SingleOrDefaultAsync(v => v.Id == visitId);

        Assert.NotNull(visitFromDb);
        Assert.Equal("First diagnosis", visitFromDb.Diagnosis);
        Assert.Equal(VisitStatus.Draft, visitFromDb.Status);
    }
}
