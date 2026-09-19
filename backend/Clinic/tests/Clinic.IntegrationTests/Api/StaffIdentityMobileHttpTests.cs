using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Clinic.Infrastructure.Authentication;
using Clinic.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Clinic.IntegrationTests.Api;

public sealed partial class StaffIdentityHttpTests
{
    private const string MobileRoute = "/api/mobile/staff/auth/";

    private static Task<HttpResponseMessage> MobileLogin(HttpClient client, string login = Name, string password = Password) =>
        client.PostAsJsonAsync(MobileRoute + "login", new { login, password });

    private static async Task<string> MobileChallenge(HttpClient client, string login = Name)
    {
        using var response = await MobileLogin(client, login);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.False(response.Headers.Contains("Set-Cookie"));
        var json = await Json(response);
        Assert.Equal("totp", json.GetProperty("next").GetString());
        Assert.False(json.TryGetProperty("accessToken", out _));
        return json.GetProperty("challenge").GetString()!;
    }

    private static async Task<string> MobileComplete(HttpClient client, string key, string login = Name)
    {
        var challenge = await MobileChallenge(client, login);
        using var response = await client.PostAsJsonAsync(MobileRoute + "mfa", new { challenge, code = Totp(key) });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.False(response.Headers.Contains("Set-Cookie"));
        var json = await Json(response);
        Assert.Equal(900, json.GetProperty("expiresIn").GetInt32());
        var token = json.GetProperty("accessToken").GetString()!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return token;
    }

    [Theory]
    [InlineData("Doctor")]
    [InlineData("DoctorAssistant")]
    [InlineData("Receptionist")]
    public async Task MobileValidUsernameAndEmailRequireMfaAndIssueOnlyMobileSession(string role)
    {
        using var web = Client();
        var enrolled = await Enroll(web);
        using (var scope = _factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<StaffAdministration>().ChangeAsync(_id, "set-grants",
                approvedRoles: [role], permissions: [], scopes: []);
            var users = scope.ServiceProvider.GetRequiredService<UserManager<StaffUser>>();
            Assert.True((await users.SetEmailAsync((await users.FindByIdAsync(_id))!, "staff@example.test")).Succeeded);
        }
        using var mobile = Client(false);
        var challenge = await MobileChallenge(mobile);
        mobile.DefaultRequestHeaders.Authorization = new("Bearer", challenge);
        Assert.Equal(HttpStatusCode.Unauthorized, (await mobile.GetAsync(MobileRoute + "session")).StatusCode);
        var token = await MobileComplete(mobile, enrolled.Key, "STAFF@example.test");
        using var session = await mobile.GetAsync(MobileRoute + "session");
        Assert.Equal(HttpStatusCode.OK, session.StatusCode);
        Assert.Equal(role, (await Json(session)).GetProperty("roles")[0].GetString());
        Assert.Equal(HttpStatusCode.Unauthorized, (await mobile.GetAsync("/api/staff/auth/session")).StatusCode);
        using var scope2 = _factory.Services.CreateScope();
        var db = scope2.ServiceProvider.GetRequiredService<ClinicDbContext>();
        var saved = await db.Set<MobileStaffSession>().SingleAsync();
        Assert.Equal(64, saved.TokenHash.Length);
        Assert.DoesNotContain(token, JsonSerializer.Serialize(saved));
        var events = await db.AuditEvents.Where(x => x.ActionCode.StartsWith("staff.mobile.")).ToListAsync();
        Assert.Contains(events, x => x.ActionCode == "staff.mobile.session.created");
        Assert.All(events, x => Assert.Empty(x.Metadata));
        var logsAndAudit = string.Join('\n', _logs.Messages) + JsonSerializer.Serialize(events);
        foreach (var secret in new[] { Password, enrolled.Key, challenge, token, Totp(enrolled.Key) })
            Assert.DoesNotContain(secret, logsAndAudit);
    }

    [Fact]
    public async Task MobileUnenrolledCannotBypassMandatoryMfa()
    {
        using var mobile = Client(false);
        using var response = await MobileLogin(mobile);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("mfa_enrollment_required", await Body(response));
        Assert.DoesNotContain("accessToken", await Body(response));
        Assert.Equal(HttpStatusCode.Unauthorized, (await mobile.GetAsync(MobileRoute + "session")).StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MobileInvalidCredentialsAndMfaPreserveSharedLockout(bool mfa)
    {
        using var web = Client();
        var enrolled = await Enroll(web);
        using var mobile = Client(false);
        var challenge = await MobileChallenge(mobile);
        for (var i = 0; i < 5; i++)
        {
            using var response = mfa
                ? await mobile.PostAsJsonAsync(MobileRoute + "mfa", new { challenge, code = "invalid" })
                : await MobileLogin(mobile, password: "wrong-password");
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Contains("invalid_credentials", await Body(response));
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await MobileLogin(mobile)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await mobile.PostAsJsonAsync(MobileRoute + "mfa", new { challenge, code = Totp(enrolled.Key) })).StatusCode);
        using var scope = _factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<StaffUser>>();
        Assert.True(await users.IsLockedOutAsync((await users.FindByIdAsync(_id))!));
    }

    [Fact]
    public async Task MobileMfaChallengeIsSingleUseAndExpires()
    {
        using var web = Client();
        var enrolled = await Enroll(web);
        using var mobile = Client(false);
        var challenge = await MobileChallenge(mobile);
        var responses = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ =>
            mobile.PostAsJsonAsync(MobileRoute + "mfa", new { challenge, code = Totp(enrolled.Key) })));
        Assert.Single(responses, x => x.StatusCode == HttpStatusCode.OK);
        Assert.Single(responses, x => x.StatusCode == HttpStatusCode.Unauthorized);
        challenge = await MobileChallenge(mobile);
        _clock.Now = _clock.Now.AddMinutes(6);
        Assert.Equal(HttpStatusCode.Unauthorized, (await mobile.PostAsJsonAsync(MobileRoute + "mfa", new { challenge, code = Totp(enrolled.Key) })).StatusCode);
        foreach (var response in responses) response.Dispose();
    }

    [Theory]
    [InlineData("disable")]
    [InlineData("revoke")]
    [InlineData("reset-mfa")]
    [InlineData("reset-password")]
    public async Task MobileAccountRevocationRejectsSessionAndPendingChallenge(string operation)
    {
        using var web = Client();
        var enrolled = await Enroll(web);
        using var mobile = Client(false);
        await MobileComplete(mobile, enrolled.Key);
        var challenge = await MobileChallenge(mobile);
        using (var scope = _factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<StaffAdministration>().ChangeAsync(_id, operation, "New-Synthetic-Password-94!");
        Assert.Equal(HttpStatusCode.Unauthorized, (await mobile.GetAsync(MobileRoute + "session")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await mobile.PostAsJsonAsync(MobileRoute + "mfa", new { challenge, code = Totp(enrolled.Key) })).StatusCode);
        if (operation == "disable") Assert.Equal(HttpStatusCode.Unauthorized, (await MobileLogin(mobile)).StatusCode);
    }

    [Fact]
    public async Task MobileLogoutRevokesOnlyCurrentSessionAndExpiryIsAbsolute()
    {
        using var web = Client();
        var enrolled = await Enroll(web);
        using var mobile = Client(false);
        var capturedToken = await MobileComplete(mobile, enrolled.Key);
        using var other = Client(false);
        await MobileComplete(other, enrolled.Key);
        Assert.Equal(HttpStatusCode.NoContent, (await mobile.PostAsync(MobileRoute + "logout", null)).StatusCode);
        mobile.DefaultRequestHeaders.Authorization = new("Bearer", capturedToken);
        Assert.Equal(HttpStatusCode.Unauthorized, (await mobile.GetAsync(MobileRoute + "session")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await other.GetAsync(MobileRoute + "session")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await web.GetAsync("/api/staff/auth/session")).StatusCode);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ClinicDbContext>();
            Assert.Equal(1, await db.Set<MobileStaffSession>().CountAsync(x => x.RevokedAtUtc != null));
            Assert.True(await db.AuditEvents.AnyAsync(x => x.ActionCode == "staff.mobile.session.revoked"));
        }
        _clock.Now = _clock.Now.AddMinutes(16);
        Assert.Equal(HttpStatusCode.Unauthorized, (await other.GetAsync(MobileRoute + "session")).StatusCode);
    }

    [Fact]
    public async Task MobileRejectsWebCookiesUnknownUsersAndForgedChallenges()
    {
        using var web = Client();
        await Enroll(web);
        Assert.Equal(HttpStatusCode.Unauthorized, (await web.GetAsync(MobileRoute + "session")).StatusCode);
        using var mobile = Client(false);
        Assert.Equal(HttpStatusCode.Unauthorized, (await MobileLogin(mobile, "unknown")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await mobile.PostAsJsonAsync(MobileRoute + "mfa", new { challenge = "forged", code = "123456" })).StatusCode);
    }

    [Fact]
    public async Task MobileRoleRemovalWithoutStampRotationRejectsSession()
    {
        using var web = Client();
        var enrolled = await Enroll(web);
        using var mobile = Client(false);
        await MobileComplete(mobile, enrolled.Key);
        using (var scope = _factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<StaffUser>>();
            var user = (await users.FindByIdAsync(_id))!;
            var stamp = user.SecurityStamp;
            Assert.True((await users.AddToRoleAsync(user, "Receptionist")).Succeeded);
            Assert.True((await users.RemoveFromRoleAsync(user, "Doctor")).Succeeded);
            Assert.Equal(stamp, user.SecurityStamp);
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await mobile.GetAsync(MobileRoute + "session")).StatusCode);
    }

    [Fact]
    public async Task MobileAmbiguousEmailFailsClosedAndNewLoginReplacesChallenge()
    {
        using var web = Client();
        var enrolled = await Enroll(web);
        using var mobile = Client(false);
        var old = await MobileChallenge(mobile);
        await MobileChallenge(mobile);
        Assert.Equal(HttpStatusCode.Unauthorized, (await mobile.PostAsJsonAsync(MobileRoute + "mfa", new { challenge = old, code = Totp(enrolled.Key) })).StatusCode);
        using (var scope = _factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<StaffUser>>();
            Assert.True((await users.SetEmailAsync((await users.FindByIdAsync(_id))!, "duplicate@example.test")).Succeeded);
            Assert.True((await users.CreateAsync(new StaffUser { UserName = "duplicate-staff", Email = "duplicate@example.test" }, Password)).Succeeded);
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await MobileLogin(mobile, "duplicate@example.test")).StatusCode);
    }
}
