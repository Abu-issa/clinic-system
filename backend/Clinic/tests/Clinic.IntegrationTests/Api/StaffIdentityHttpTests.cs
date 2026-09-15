using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Clinic.Infrastructure.Authentication;
using Clinic.Infrastructure.Persistence;
using Clinic.Domain.Entities;
using Clinic.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Clinic.IntegrationTests.Api;

// Every browser credential in this class is issued by the actual HTTP/Identity flow.
public sealed partial class StaffIdentityHttpTests : IAsyncLifetime
{
    private const string Password = "Synthetic-Password-93!";
    private const string Name = "synthetic-doctor";
    private readonly SqlDatabaseFixture _database = new();
    private readonly PolicyTestClock _clock = new() { Now = DateTimeOffset.UtcNow };
    private readonly CaptureLogs _logs = new();
    private WebApplicationFactory<Program> _factory = null!;
    private Guid _doctor;
    private string _id = null!;
    public async Task InitializeAsync()
    {
        await _database.InitializeAsync();
        _factory = new BookingApiFactory(_database.ConnectionString).WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => { services.AddSingleton<TimeProvider>(_clock); services.AddSingleton<ILoggerProvider>(_logs); }));
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ClinicDbContext>();
        var doctor = new Doctor("Synthetic responsible doctor");
        db.Add(doctor);
        await db.SaveChangesAsync();
        _doctor = doctor.Id;
        _id = await scope.ServiceProvider.GetRequiredService<StaffAdministration>()
            .ProvisionFirstDoctorAsync(Name, Password, _doctor, [_doctor]);
    }
    public async Task DisposeAsync() { _factory.Dispose(); await _database.DisposeAsync(); }
    private HttpClient Client(bool cookies = true) => _factory.CreateClient(new WebApplicationFactoryClientOptions
    { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false, HandleCookies = cookies });
    private static async Task<string> Body(HttpResponseMessage response) => await response.Content.ReadAsStringAsync();
    private static async Task<JsonElement> Json(HttpResponseMessage response) => JsonDocument.Parse(await Body(response)).RootElement.Clone();
    private static void Token(HttpClient client, string token)
    { client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN"); client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", token); }
    private static async Task Csrf(HttpClient client)
    {
        using var response = await client.GetAsync("/api/staff/auth/csrf");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Token(client, (await Json(response)).GetProperty("requestToken").GetString()!);
    }
    private async Task<HttpResponseMessage> Login(HttpClient client, string password = Password)
    {
        await Csrf(client);
        var response = await client.PostAsJsonAsync("/api/staff/auth/login", new { userName = Name, password });
        if (response.IsSuccessStatusCode) Token(client, (await Json(response)).GetProperty("csrfToken").GetString()!);
        return response;
    }
    private async Task<(string Key, string[] Codes, string Cookie, string Challenge)> Enroll(HttpClient client)
    {
        using var login = await Login(client);
        Assert.Equal("enrollment", (await Json(login)).GetProperty("next").GetString());
        using var setup = await client.PostAsync("/api/staff/auth/enrollment/setup", null);
        Assert.Equal(HttpStatusCode.OK, setup.StatusCode);
        Assert.True(setup.Headers.CacheControl?.NoStore);
        var key = (await Json(setup)).GetProperty("sharedKey").GetString()!;
        using var verify = await client.PostAsJsonAsync("/api/staff/auth/enrollment/verify", new { code = Totp(key) });
        Assert.Equal(HttpStatusCode.OK, verify.StatusCode);
        Assert.True(verify.Headers.CacheControl?.NoStore);
        var json = await Json(verify);
        Token(client, json.GetProperty("csrfToken").GetString()!);
        return (key, json.GetProperty("recoveryCodes").EnumerateArray().Select(x => x.GetString()!).ToArray(), Cookie(verify, "__Host-Clinic.Staff="), Cookie(login, "__Host-Clinic.StaffIntermediate="));
    }
    private static string Cookie(HttpResponseMessage response, string prefix) =>
        response.Headers.GetValues("Set-Cookie").Single(x => x.StartsWith(prefix) && !x.StartsWith(prefix + ";")).Split(';')[0];
    private static string Totp(string key)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bytes = new List<byte>(); int bits = 0, buffer = 0;
        foreach (var c in key.TrimEnd('=')) { buffer = (buffer << 5) | alphabet.IndexOf(c); bits += 5; if (bits >= 8) { bits -= 8; bytes.Add((byte)(buffer >> bits)); } }
        var counter = BitConverter.GetBytes(IPAddress.HostToNetworkOrder(DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30));
        var hash = HMACSHA1.HashData(bytes.ToArray(), counter);
        var offset = hash[^1] & 15;
        var number = ((hash[offset] & 127) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (number % 1000000).ToString("D6");
    }

    [Fact]
    public async Task ProvisioningRerunPreservesCredentialsAndExplicitGrants()
    {
        using var client = Client();
        var enrolled = await Enroll(client);
        using var scope = _factory.Services.CreateScope();
        var admin = scope.ServiceProvider.GetRequiredService<StaffAdministration>();
        Assert.Equal(_id, await admin.ProvisionFirstDoctorAsync(Name, "Must-not-reset-123!", _doctor, [_doctor]));
        var manager = scope.ServiceProvider.GetRequiredService<UserManager<StaffUser>>();
        var user = (await manager.FindByIdAsync(_id))!;
        Assert.True(await manager.CheckPasswordAsync(user, Password));
        Assert.Equal(enrolled.Key, await manager.GetAuthenticatorKeyAsync(user));
        Assert.True(user.TwoFactorEnabled);
        await Assert.ThrowsAsync<ArgumentException>(() => admin.ProvisionFirstDoctorAsync(Name, Password, _doctor, [Guid.NewGuid()]));
    }

    [Fact]
    public async Task PasswordAndEnrollmentAreRestricted_ThenMfaAllowsScopedAccess()
    {
        using var client = Client();
        using var login = await Login(client);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        using var restricted = await client.GetAsync("/api/staff/auth/session");
        Assert.Equal(HttpStatusCode.Unauthorized, restricted.StatusCode);
        Assert.True(restricted.Headers.CacheControl?.NoStore);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync($"/api/staff/doctors/{_doctor}/availability?date=2030-01-07&appointmentType=Consultation")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/staff/appointments", new { })).StatusCode);
        var enrolled = await Enroll(client);
        using var session = await client.GetAsync("/api/staff/auth/session");
        Assert.Equal(HttpStatusCode.OK, session.StatusCode);
        Assert.Equal(new[] { "roles", "staffId" }, (await Json(session)).EnumerateObject().Select(x => x.Name).Order());
        var date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(_clock.Now, TestWorkingHours.ClinicTimeZone).DateTime).ToString("yyyy-MM-dd");
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/staff/doctors/{_doctor}/availability?date={date}&appointmentType=Consultation")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/staff/doctors/{Guid.NewGuid()}/availability?date={date}&appointmentType=Consultation")).StatusCode);
        using var other = Client();
        using var password = await Login(other);
        Assert.Equal("totp", (await Json(password)).GetProperty("next").GetString());
        Assert.Equal(HttpStatusCode.Unauthorized, (await other.GetAsync("/api/staff/auth/session")).StatusCode);
        using var totp = await other.PostAsJsonAsync("/api/staff/auth/totp", new { code = Totp(enrolled.Key) });
        Assert.Equal(HttpStatusCode.OK, totp.StatusCode);
        Assert.DoesNotContain("recoveryCodes", await Body(totp));
        Assert.Equal(HttpStatusCode.OK, (await other.GetAsync("/api/staff/auth/session")).StatusCode);
        var normal = await Body(session) + await Body(password) + await Body(totp) + string.Join('\n', _logs.Messages);
        Assert.DoesNotContain(Password, normal);
        Assert.DoesNotContain(enrolled.Key, normal);
        foreach (var code in enrolled.Codes) Assert.DoesNotContain(code, normal);
        foreach (var token in new[] { enrolled.Cookie, (await Json(totp)).GetProperty("csrfToken").GetString()! })
            Assert.DoesNotContain(token, string.Join('\n', _logs.Messages));
    }

    [Theory]
    [InlineData("login")]
    [InlineData("enrollment/setup")]
    [InlineData("enrollment/verify")]
    [InlineData("totp")]
    [InlineData("recovery")]
    [InlineData("logout")]
    public async Task StateChangesRequireCsrf(string route)
    {
        using var client = Client();
        using var response = await client.PostAsJsonAsync("/api/staff/auth/" + route, new { userName = Name, password = Password, code = "123456" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("invalid_csrf_token", await Body(response));
        Token(client, "forged");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/staff/auth/" + route, new { userName = Name, password = Password, code = "123456" })).StatusCode);
    }

    [Fact]
    public async Task LoginIgnoresForgedGrantsAndHeaders()
    {
        using var first = Client();
        var enrolled = await Enroll(first);
        using var scope = _factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<StaffAdministration>().ChangeAsync(_id, "set-grants",
            approvedRoles: ["DoctorAssistant"], permissions: [], scopes: [_doctor]);
        Assert.Equal(HttpStatusCode.Unauthorized, (await first.GetAsync("/api/staff/auth/session")).StatusCode);
        using var client = Client();
        await Csrf(client);
        client.DefaultRequestHeaders.Add("X-Role", "Doctor");
        using var login = await client.PostAsJsonAsync("/api/staff/auth/login", new { userName = Name, password = Password,
            roles = new[] { "Doctor" }, permission = "schedule.manage", appointment_doctor_id = _doctor });
        Token(client, (await Json(login)).GetProperty("csrfToken").GetString()!);
        using var verified = await client.PostAsJsonAsync("/api/staff/auth/totp", new { code = Totp(enrolled.Key), amr = "mfa", role = "Doctor" });
        Assert.Equal(HttpStatusCode.OK, verified.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/staff/doctors/{_doctor}/availability?date=2030-01-07&appointmentType=Consultation")).StatusCode);
        Assert.DoesNotContain("\"Doctor\"", await Body(await client.GetAsync("/api/staff/auth/session")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IncorrectCredentialsLockOutWithoutPasswordOnlyReset(bool totp)
    {
        using var client = Client();
        if (totp) { await Enroll(client); await Login(client); }
        for (var i = 0; i < 5; i++)
        {
            using var response = totp ? await client.PostAsJsonAsync("/api/staff/auth/totp", new { code = "not-a-code" }) : await Login(client, "wrong-password");
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Contains("invalid_credentials", await Body(response));
        }
        using var locked = await Login(client);
        Assert.Equal(HttpStatusCode.Unauthorized, locked.StatusCode);
        using var scope = _factory.Services.CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<UserManager<StaffUser>>();
        Assert.True(await manager.IsLockedOutAsync((await manager.FindByIdAsync(_id))!));
    }

    [Theory]
    [InlineData("login")]
    [InlineData("totp")]
    [InlineData("recovery")]
    [InlineData("enrollment/verify")]
    public async Task RateLimitIsBoundedAndReturnsProblemDetails(string route)
    {
        using var client = Client();
        await Csrf(client);
        for (var i = 0; i < 29; i++) Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/staff/auth/csrf")).StatusCode);
        using var response = await client.PostAsJsonAsync("/api/staff/auth/" + route, new { userName = Name, password = Password, code = "123456" });
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Contains("rate_limited", await Body(response));
    }

    [Fact]
    public async Task IntermediateExpiresAndCannotBeReplayedAfterSuccess()
    {
        using var client = Client();
        using var login = await Login(client);
        var challenge = Cookie(login, "__Host-Clinic.StaffIntermediate=");
        using var replay = Client();
        replay.DefaultRequestHeaders.Add("Cookie", challenge);
        _clock.Now = _clock.Now.AddMinutes(6);
        Assert.Equal(HttpStatusCode.Unauthorized, (await replay.GetAsync("/api/staff/auth/session")).StatusCode);
        await Csrf(client);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync("/api/staff/auth/enrollment/setup", null)).StatusCode);
        _clock.Now = DateTimeOffset.UtcNow;
        var enrolled = await Enroll(client);
        // A consumed or replaced challenge no longer authenticates even with a valid protected cookie.
        Assert.Equal(HttpStatusCode.Unauthorized, (await replay.GetAsync("/api/staff/auth/session")).StatusCode);
        using var scope = _factory.Services.CreateScope();
        var user = await scope.ServiceProvider.GetRequiredService<UserManager<StaffUser>>().FindByIdAsync(_id);
        Assert.Null(user!.ChallengeId);
        using var consumed = Client();
        consumed.DefaultRequestHeaders.Add("Cookie", enrolled.Challenge);
        await Csrf(consumed);
        Assert.Equal(HttpStatusCode.Unauthorized, (await consumed.PostAsJsonAsync("/api/staff/auth/enrollment/verify", new { code = Totp(enrolled.Key) })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await consumed.PostAsync("/api/staff/auth/enrollment/setup", null)).StatusCode);
    }

    [Theory]
    [InlineData("disable")]
    [InlineData("revoke")]
    [InlineData("reset-mfa")]
    public async Task AccountRevocationInvalidatesPendingSecondFactor(string operation)
    {
        using var client = Client();
        var enrolled = await Enroll(client);
        await Login(client);
        using (var scope = _factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<StaffAdministration>().ChangeAsync(_id, operation);
        await Csrf(client);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/staff/auth/recovery", new { code = enrolled.Codes[0] })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/staff/auth/totp", new { code = Totp(enrolled.Key) })).StatusCode);
        if (operation == "disable") Assert.Equal(HttpStatusCode.Unauthorized, (await Login(client)).StatusCode);
    }

    [Fact]
    public async Task OldAnonymousCsrfIsRejectedAfterIdentityChanges_AndEnrollmentLockoutApplies()
    {
        using var client = Client();
        await Csrf(client);
        var anonymous = client.DefaultRequestHeaders.GetValues("X-CSRF-TOKEN").Single();
        using var login = await Login(client);
        Token(client, anonymous);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/api/staff/auth/enrollment/setup", null)).StatusCode);
        Token(client, (await Json(login)).GetProperty("csrfToken").GetString()!);
        for (var i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/staff/auth/enrollment/verify", new { code = "not-a-code" })).StatusCode);
        using var scope = _factory.Services.CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<UserManager<StaffUser>>();
        var user = (await manager.FindByIdAsync(_id))!;
        Assert.False(user.TwoFactorEnabled);
        Assert.True(await manager.IsLockedOutAsync(user));
        Assert.Null(user.ChallengeId);
    }

    [Fact]
    public async Task RecoveryCodesAreSingleUseAndConcurrentChallengeConsumptionIsAtomic()
    {
        using var client = Client();
        var enrolled = await Enroll(client);
        using var login = await Login(client);
        using var success = await client.PostAsJsonAsync("/api/staff/auth/recovery", new { code = enrolled.Codes[0] });
        Assert.Equal(HttpStatusCode.OK, success.StatusCode);
        Assert.DoesNotContain("recoveryCodes", await Body(success));
        await Login(client);
        using var reused = await client.PostAsJsonAsync("/api/staff/auth/recovery", new { code = enrolled.Codes[0] });
        Assert.Equal(HttpStatusCode.Unauthorized, reused.StatusCode);
        // Same intermediate cookie and antiforgery identity, raced through separate HTTP requests.
        var tasks = Enumerable.Range(0, 2).Select(_ => client.PostAsJsonAsync("/api/staff/auth/recovery", new { code = enrolled.Codes[1] })).ToArray();
        var responses = await Task.WhenAll(tasks);
        Assert.Single(responses, x => x.StatusCode == HttpStatusCode.OK);
        Assert.Single(responses, x => x.StatusCode == HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("logout")]
    [InlineData("revoke-sessions")]
    [InlineData("disable")]
    [InlineData("revoke")]
    [InlineData("reset-password")]
    [InlineData("reset-mfa")]
    [InlineData("stamp")]
    public async Task RevocationIsCheckedOnTheVeryNextRequest(string operation)
    {
        using var client = Client();
        var enrolled = await Enroll(client);
        using var captured = Client(false);
        captured.DefaultRequestHeaders.Add("Cookie", enrolled.Cookie);
        Assert.Equal(HttpStatusCode.OK, (await captured.GetAsync("/api/staff/auth/session")).StatusCode);
        if (operation is "logout" or "revoke-sessions")
        {
            client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/api/staff/auth/" + operation, null)).StatusCode);
            await Csrf(client);
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/staff/auth/" + operation, null)).StatusCode);
        }
        else
        {
            using var scope = _factory.Services.CreateScope();
            if (operation == "stamp")
            {
                var users = scope.ServiceProvider.GetRequiredService<UserManager<StaffUser>>();
                Assert.True((await users.UpdateSecurityStampAsync((await users.FindByIdAsync(_id))!)).Succeeded);
            }
            else await scope.ServiceProvider.GetRequiredService<StaffAdministration>().ChangeAsync(_id, operation, "New-Synthetic-Password-94!");
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await captured.GetAsync("/api/staff/auth/session")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await captured.GetAsync($"/api/staff/doctors/{_doctor}/availability?date=2030-01-07&appointmentType=Consultation")).StatusCode);
    }

    [Fact]
    public async Task IdentityMigrationPreservesExistingMedicalRows()
    {
        // Own database; this test intentionally downgrades only its isolated fixture.
        await using var db = _database.CreateContext();
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20260914160508_AddAppointmentType");
        var patient = new Patient("Synthetic migration patient", "0790000000");
        var appointment = new Appointment(patient.Id, _doctor, DateTimeOffset.UtcNow.AddDays(1), DateTimeOffset.UtcNow.AddDays(1).AddMinutes(47));
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO Patients (Id, FullName, PhoneNumber, CreatedAtUtc) VALUES ({patient.Id}, {patient.FullName}, {patient.PhoneNumber}, {patient.CreatedAtUtc})");
        db.Add(appointment);
        await db.SaveChangesAsync();
        var version = appointment.RowVersion.ToArray();
        await migrator.MigrateAsync();
        db.ChangeTracker.Clear();
        var saved = await db.Appointments.SingleAsync(x => x.Id == appointment.Id);
        Assert.Null(saved.Type);
        Assert.Equal(appointment.StartsAtUtc, saved.StartsAtUtc);
        Assert.Equal(appointment.EndsAtUtc, saved.EndsAtUtc);
        Assert.Equal(version, saved.RowVersion);
        Assert.True(await db.Doctors.AnyAsync(x => x.Id == _doctor));
        Assert.False(await db.Users.AnyAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AdministrativePasswordsMustFitTheLoginContract(bool provisioning)
    {
        using var scope = _factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<StaffUser>>();
        var admin = scope.ServiceProvider.GetRequiredService<StaffAdministration>();
        var user = (await users.FindByIdAsync(_id))!;
        var tooLong = Password + new string('a', 1025);
        if (provisioning)
        {
            Assert.True((await users.DeleteAsync(user)).Succeeded); // This test's isolated account only.
            await Assert.ThrowsAsync<InvalidOperationException>(() => admin.ProvisionFirstDoctorAsync(Name, tooLong, _doctor, [_doctor]));
            await using var verification = _database.CreateContext();
            Assert.False(await verification.Users.AnyAsync());
        }
        else
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => admin.ChangeAsync(_id, "reset-password", tooLong));
            using var verification = _factory.Services.CreateScope();
            var manager = verification.ServiceProvider.GetRequiredService<UserManager<StaffUser>>();
            Assert.True(await manager.CheckPasswordAsync((await manager.FindByIdAsync(_id))!, Password));
        }
    }

    [Fact]
    public async Task CookieSchemesCannotBeSubstituted()
    {
        using var client = Client();
        var enrolled = await Enroll(client);
        using var login = await Login(client);
        var challenge = Cookie(login, "__Host-Clinic.StaffIntermediate=");
        using var substitutedFull = Client();
        substitutedFull.DefaultRequestHeaders.Add("Cookie", challenge.Replace("__Host-Clinic.StaffIntermediate=", "__Host-Clinic.Staff="));
        Assert.Equal(HttpStatusCode.Unauthorized, (await substitutedFull.GetAsync("/api/staff/auth/session")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await substitutedFull.PostAsJsonAsync("/api/staff/appointments", new { })).StatusCode);

        using var substitutedIntermediate = Client();
        substitutedIntermediate.DefaultRequestHeaders.Add("Cookie", enrolled.Cookie.Replace("__Host-Clinic.Staff=", "__Host-Clinic.StaffIntermediate="));
        await Csrf(substitutedIntermediate);
        Assert.Equal(HttpStatusCode.Unauthorized, (await substitutedIntermediate.PostAsync("/api/staff/auth/enrollment/setup", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await substitutedIntermediate.PostAsJsonAsync("/api/staff/auth/totp", new { code = Totp(enrolled.Key) })).StatusCode);
    }

    [Fact]
    public async Task RepeatedSetupPreservesKeyAndUsesOnlyEnrollmentIdentity()
    {
        using var client = Client();
        await Login(client);
        using var first = await client.PostAsync("/api/staff/auth/enrollment/setup", null);
        var key = (await Json(first)).GetProperty("sharedKey").GetString();
        using var second = await client.PostAsJsonAsync("/api/staff/auth/enrollment/setup?userId=untrusted", new { userName = "another-user", sharedKey = "untrusted" });
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(key, (await Json(second)).GetProperty("sharedKey").GetString());
        await Login(client); // Replacing the challenge must not replace the pending key.
        using var third = await client.PostAsync("/api/staff/auth/enrollment/setup", null);
        Assert.Equal(key, (await Json(third)).GetProperty("sharedKey").GetString());
        using var scope = _factory.Services.CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<UserManager<StaffUser>>();
        var user = (await manager.FindByIdAsync(_id))!;
        Assert.False(user.TwoFactorEnabled);
        Assert.Equal(key, await manager.GetAuthenticatorKeyAsync(user));
        using var completed = await client.PostAsJsonAsync("/api/staff/auth/enrollment/verify", new { code = Totp(key!) });
        Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
        Token(client, (await Json(completed)).GetProperty("csrfToken").GetString()!);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync("/api/staff/auth/enrollment/setup", null)).StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentSecondFactorProducesOnlyOneFullSession(bool recovery)
    {
        using var original = Client();
        var enrolled = await Enroll(original);
        using var login = await Login(original);
        var challenge = Cookie(login, "__Host-Clinic.StaffIntermediate=");
        using var first = Client();
        using var second = Client();
        first.DefaultRequestHeaders.Add("Cookie", challenge);
        second.DefaultRequestHeaders.Add("Cookie", challenge);
        await Csrf(first);
        await Csrf(second);
        var route = recovery ? "recovery" : "totp";
        var code = recovery ? enrolled.Codes[0] : Totp(enrolled.Key);
        var responses = await Task.WhenAll(
            first.PostAsJsonAsync("/api/staff/auth/" + route, new { code }),
            second.PostAsJsonAsync("/api/staff/auth/" + route, new { code }));
        Assert.Single(responses, x => x.StatusCode == HttpStatusCode.OK);
        var rejected = Assert.Single(responses, x => x.StatusCode == HttpStatusCode.Unauthorized);
        Assert.False(rejected.Headers.TryGetValues("Set-Cookie", out var cookies) && cookies.Any(x => x.StartsWith("__Host-Clinic.Staff=")));
        using var replay = Client();
        replay.DefaultRequestHeaders.Add("Cookie", challenge);
        await Csrf(replay);
        Assert.Equal(HttpStatusCode.Unauthorized, (await replay.PostAsJsonAsync("/api/staff/auth/" + route, new { code })).StatusCode);
        foreach (var response in responses) response.Dispose();
    }

    private sealed class CaptureLogs : ILoggerProvider
    {
        public System.Collections.Concurrent.ConcurrentBag<string> Messages { get; } = [];
        public ILogger CreateLogger(string categoryName) => new Sink(Messages);
        public void Dispose() { }
        private sealed class Sink(System.Collections.Concurrent.ConcurrentBag<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => messages.Add(formatter(state, exception) + (exception is null ? "" : Environment.NewLine + exception));
        }
    }
}
