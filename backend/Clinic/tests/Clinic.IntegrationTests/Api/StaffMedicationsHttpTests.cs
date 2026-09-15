using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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

// Real HTTP tests for the clinic-wide medication catalog: authentication, persisted
// authorization revalidation, CSRF, optimistic concurrency, and bounded search.
public sealed class StaffMedicationsHttpTests : IAsyncLifetime
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

    private const string Password = "Synthetic-Medication-Password-99!";
    private const string DoctorName = "synthetic-med-doctor";
    private const string AssistantName = "synthetic-med-assistant";
    private const string ReceptionistName = "synthetic-med-receptionist";
    private static readonly string[] DoctorPermissions = ["medications.read", "medications.manage"];
    private static readonly string[] AssistantPermissions = ["medications.read"];
    private readonly SqlDatabaseFixture _database = new();
    private readonly CaptureLogs _logs = new();
    private WebApplicationFactory<Program> _factory = null!;
    private string _doctorUserId = null!;
    private string _assistantUserId = null!;
    private string _receptionistUserId = null!;

    public async Task InitializeAsync()
    {
        await _database.InitializeAsync();
        _factory = new BookingApiFactory(_database.ConnectionString).WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
                services.AddSingleton<ILoggerProvider>(_logs)));

        using var scope = _factory.Services.CreateScope();
        var admin = scope.ServiceProvider.GetRequiredService<StaffAdministration>();
        var users = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<StaffUser>>();

        var doctorId = await NewDoctorAsync(scope);
        _doctorUserId = await admin.ProvisionFirstDoctorAsync(DoctorName, Password, doctorId, [doctorId]);
        await admin.ChangeAsync(_doctorUserId, "set-grants",
            approvedRoles: ["Doctor"], permissions: DoctorPermissions, scopes: null, patientScopes: []);

        var assistant = new StaffUser { UserName = AssistantName, LockoutEnabled = true };
        Assert.True((await users.CreateAsync(assistant, Password)).Succeeded);
        _assistantUserId = assistant.Id;
        await admin.ChangeAsync(_assistantUserId, "set-grants",
            approvedRoles: ["DoctorAssistant"], permissions: AssistantPermissions, scopes: null, patientScopes: []);

        var receptionist = new StaffUser { UserName = ReceptionistName, LockoutEnabled = true };
        Assert.True((await users.CreateAsync(receptionist, Password)).Succeeded);
        _receptionistUserId = receptionist.Id;
        await admin.ChangeAsync(_receptionistUserId, "set-grants",
            approvedRoles: ["Receptionist"], permissions: [], scopes: null, patientScopes: []);
    }

    private static async Task<Guid> NewDoctorAsync(IServiceScope scope)
    {
        var db = scope.ServiceProvider.GetRequiredService<ClinicDbContext>();
        var doctor = new Clinic.Domain.Entities.Doctor($"Synthetic Medication Doctor {Guid.NewGuid():N}");
        db.Doctors.Add(doctor);
        await db.SaveChangesAsync();
        return doctor.Id;
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

    private static object CreateBody(string name, string strength = "500", string unit = "mg") => new
    {
        GenericNameEn = name,
        GenericNameAr = (string?)null,
        BrandNameEn = (string?)"Synthetic-Brand",
        BrandNameAr = (string?)null,
        Strength = strength,
        Unit = unit,
        Form = (int)DosageForm.Tablet,
        Route = (int)MedicationRoute.Oral,
        Category = (string?)"Analgesic"
    };

    private async Task<JsonElement> CreateMedication(HttpClient client, string name)
    {
        var response = await client.PostAsJsonAsync("/api/staff/medications", CreateBody(name));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await Json(response);
    }

    [Fact]
    public async Task AnonymousRequestIsUnauthorized()
    {
        using var client = Client();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/staff/medications")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.PostAsJsonAsync("/api/staff/medications", CreateBody("Anonymous"))).StatusCode);
    }

    [Fact]
    public async Task IntermediateNonMfaSessionIsUnauthorized()
    {
        using var client = Client();
        await Csrf(client);
        using var login = await client.PostAsJsonAsync("/api/staff/auth/login", new { userName = DoctorName, password = Password });
        Assert.True(login.IsSuccessStatusCode);
        Token(client, (await Json(login)).GetProperty("csrfToken").GetString()!);

        // Password-verified intermediate session has no MFA claim yet.
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/staff/medications")).StatusCode);
    }

    [Fact]
    public async Task MissingAndInvalidCsrfAreRejected()
    {
        using var client = await EnrolledClient(DoctorName);
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        var missing = await client.PostAsJsonAsync("/api/staff/medications", CreateBody("Csrf-Missing"));
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Equal("invalid_csrf_token", (await Json(missing)).GetProperty("code").GetString());

        Token(client, "not-a-real-token");
        var invalid = await client.PostAsJsonAsync("/api/staff/medications", CreateBody("Csrf-Invalid"));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal("invalid_csrf_token", (await Json(invalid)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task AuthorizedSearchAndDetailsRoundTrip()
    {
        using var client = await EnrolledClient(DoctorName);
        var created = await CreateMedication(client, "SearchRoundTrip");
        var id = created.GetProperty("id").GetGuid();
        Assert.True(created.GetProperty("isActive").GetBoolean());
        Assert.Equal("500", created.GetProperty("strength").GetString());
        Assert.Equal("mg", created.GetProperty("unit").GetString());
        Assert.Equal(8, Convert.FromBase64String(created.GetProperty("rowVersion").GetString()!).Length);

        var details = await client.GetAsync($"/api/staff/medications/{id}");
        Assert.Equal(HttpStatusCode.OK, details.StatusCode);
        Assert.True(details.Headers.CacheControl?.NoStore);
        Assert.Equal(id, (await Json(details)).GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task DoctorAssistantWithMedicationsReadCanReadButNotMutate()
    {
        using var doctor = await EnrolledClient(DoctorName);
        var created = await CreateMedication(doctor, "AssistantRead");
        var id = created.GetProperty("id").GetGuid();

        using var assistant = await EnrolledClient(AssistantName);
        Assert.Equal(HttpStatusCode.OK, (await assistant.GetAsync($"/api/staff/medications/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await assistant.GetAsync("/api/staff/medications?query=AssistantRead")).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden,
            (await assistant.PostAsJsonAsync("/api/staff/medications", CreateBody("Assistant-Create"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await assistant.PostAsJsonAsync($"/api/staff/medications/{id}/deactivate",
                new { ExpectedRowVersion = created.GetProperty("rowVersion").GetString() })).StatusCode);
    }

    [Fact]
    public async Task ReceptionistCannotReadCatalog()
    {
        using var client = await EnrolledClient(ReceptionistName);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/staff/medications")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/staff/medications/{Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task DoctorWithoutMedicationsManageCannotMutate()
    {
        using var scope = _factory.Services.CreateScope();
        var admin = scope.ServiceProvider.GetRequiredService<StaffAdministration>();
        await admin.ChangeAsync(_doctorUserId, "set-grants",
            approvedRoles: ["Doctor"], permissions: ["medications.read"], scopes: null, patientScopes: []);

        using var client = await EnrolledClient(DoctorName);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.PostAsJsonAsync("/api/staff/medications", CreateBody("NoManage"))).StatusCode);
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
                new System.Security.Claims.Claim("permission", "medications.read"))).Succeeded);
        }

        // The cookie itself is unchanged; persisted revalidation must reject it.
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/staff/medications")).StatusCode);
    }

    [Fact]
    public async Task DisabledAccountCannotUseAnExistingSession()
    {
        using var client = await EnrolledClient(DoctorName);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/staff/medications")).StatusCode);

        using (var scope = _factory.Services.CreateScope())
        {
            var admin = scope.ServiceProvider.GetRequiredService<StaffAdministration>();
            await admin.ChangeAsync(_doctorUserId, "disable");
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/staff/medications")).StatusCode);
    }

    [Fact]
    public async Task UpdateDeactivateActivateRoundTripWithVersions()
    {
        using var client = await EnrolledClient(DoctorName);
        var created = await CreateMedication(client, "LifecycleRoundTrip");
        var id = created.GetProperty("id").GetGuid();
        var version = created.GetProperty("rowVersion").GetString()!;

        var update = await client.PutAsJsonAsync($"/api/staff/medications/{id}", new
        {
            GenericNameEn = "LifecycleRoundTrip",
            GenericNameAr = (string?)null,
            BrandNameEn = (string?)null,
            BrandNameAr = (string?)null,
            Strength = "400",
            Unit = "mg",
            Form = (int)DosageForm.Capsule,
            Route = (int)MedicationRoute.Oral,
            Category = (string?)null,
            ExpectedRowVersion = version
        });
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        var updated = await Json(update);
        Assert.Equal("400", updated.GetProperty("strength").GetString());
        version = updated.GetProperty("rowVersion").GetString()!;

        var deactivate = await client.PostAsJsonAsync($"/api/staff/medications/{id}/deactivate", new { ExpectedRowVersion = version });
        Assert.Equal(HttpStatusCode.OK, deactivate.StatusCode);
        var deactivated = await Json(deactivate);
        Assert.False(deactivated.GetProperty("isActive").GetBoolean());

        var activate = await client.PostAsJsonAsync($"/api/staff/medications/{id}/activate",
            new { ExpectedRowVersion = deactivated.GetProperty("rowVersion").GetString() });
        Assert.Equal(HttpStatusCode.OK, activate.StatusCode);
        Assert.True((await Json(activate)).GetProperty("isActive").GetBoolean());
    }

    [Fact]
    public async Task DuplicateMedicationReturns409()
    {
        using var client = await EnrolledClient(DoctorName);
        await CreateMedication(client, "DuplicateCheck");
        var duplicate = await client.PostAsJsonAsync("/api/staff/medications", CreateBody("DuplicateCheck"));
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal("duplicate_medication", (await Json(duplicate)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task StaleRowVersionReturns409()
    {
        using var client = await EnrolledClient(DoctorName);
        var created = await CreateMedication(client, "StaleVersion");
        var id = created.GetProperty("id").GetGuid();
        var staleVersion = created.GetProperty("rowVersion").GetString()!;

        var deactivate = await client.PostAsJsonAsync($"/api/staff/medications/{id}/deactivate",
            new { ExpectedRowVersion = staleVersion });
        Assert.Equal(HttpStatusCode.OK, deactivate.StatusCode);

        // The pre-deactivation version is now stale; the idempotent deactivation must still lose.
        var again = await client.PostAsJsonAsync($"/api/staff/medications/{id}/deactivate",
            new { ExpectedRowVersion = staleVersion });
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("medication_changed", (await Json(again)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task MalformedOrMissingRowVersionReturns400()
    {
        using var client = await EnrolledClient(DoctorName);
        var created = await CreateMedication(client, "MalformedVersion");
        var id = created.GetProperty("id").GetGuid();

        var notBase64 = await client.PostAsJsonAsync($"/api/staff/medications/{id}/deactivate",
            new { ExpectedRowVersion = "!!!not-base64!!!" });
        Assert.Equal(HttpStatusCode.BadRequest, notBase64.StatusCode);

        var missing = await client.PostAsJsonAsync($"/api/staff/medications/{id}/deactivate", new { });
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Equal("invalid_row_version", (await Json(missing)).GetProperty("code").GetString());

        var wrongLength = await client.PostAsJsonAsync($"/api/staff/medications/{id}/deactivate",
            new { ExpectedRowVersion = Convert.ToBase64String(new byte[4]) });
        Assert.Equal(HttpStatusCode.BadRequest, wrongLength.StatusCode);
        Assert.Equal("invalid_row_version", (await Json(wrongLength)).GetProperty("code").GetString());

        // A JSON array instead of a Base64 string fails model binding and must stay a 400.
        using var arrayBody = new StringContent(
            """{"expectedRowVersion": [1, 2, 3]}""", System.Text.Encoding.UTF8, "application/json");
        var arrayVersion = await client.PostAsync($"/api/staff/medications/{id}/deactivate", arrayBody);
        Assert.Equal(HttpStatusCode.BadRequest, arrayVersion.StatusCode);
    }

    [Fact]
    public async Task SearchIsBoundedAndDeterministicallyOrdered()
    {
        using var client = await EnrolledClient(DoctorName);
        foreach (var name in new[] { "PagingC", "PagingA", "PagingB" })
            await CreateMedication(client, name);

        var page = await client.GetAsync("/api/staff/medications?query=Paging&activeOnly=true&skip=0&take=2");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.True(page.Headers.CacheControl?.NoStore);
        var items = await Json(page);
        Assert.Equal(2, items.GetArrayLength());
        Assert.True(items.EnumerateArray().Select(x => x.GetProperty("genericNameEn").GetString()).SequenceEqual(
            items.EnumerateArray().Select(x => x.GetProperty("genericNameEn").GetString()).Order()));

        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/staff/medications?take=101")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/staff/medications?skip=-1")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/staff/medications?take=0")).StatusCode);
    }

    [Fact]
    public async Task UnknownMedicationReturns404()
    {
        using var client = await EnrolledClient(DoctorName);
        var response = await client.GetAsync($"/api/staff/medications/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("medication_not_found", (await Json(response)).GetProperty("code").GetString());
    }
}
