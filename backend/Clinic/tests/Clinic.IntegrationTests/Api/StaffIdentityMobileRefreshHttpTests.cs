using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Clinic.Infrastructure.Authentication;
using Clinic.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Clinic.IntegrationTests.Api;

public sealed partial class StaffIdentityHttpTests
{
    private sealed record MobileTokens(string AccessToken, string RefreshToken,
        DateTimeOffset ExpiresAtUtc, DateTimeOffset RefreshExpiresAtUtc);

    private static async Task<MobileTokens> ReadTokens(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.False(response.Headers.Contains("Set-Cookie"));
        var json = await Json(response);
        Assert.Equal("Bearer", json.GetProperty("tokenType").GetString());
        Assert.InRange(json.GetProperty("expiresIn").GetInt32(), 1, 900);
        return new(json.GetProperty("accessToken").GetString()!, json.GetProperty("refreshToken").GetString()!,
            json.GetProperty("expiresAtUtc").GetDateTimeOffset(), json.GetProperty("refreshExpiresAtUtc").GetDateTimeOffset());
    }

    private static async Task<MobileTokens> IssueMobileTokens(HttpClient client, string key)
    {
        var challenge = await MobileChallenge(client);
        using var response = await client.PostAsJsonAsync(MobileRoute + "mfa", new { challenge, code = Totp(key) });
        return await ReadTokens(response);
    }

    private static Task<HttpResponseMessage> RefreshMobile(HttpClient client, string refreshToken) =>
        client.PostAsJsonAsync(MobileRoute + "refresh", new { refreshToken });

    private static async Task<HttpStatusCode> MobileAccess(HttpClient client, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, MobileRoute + "session");
        request.Headers.Authorization = new("Bearer", token);
        using var response = await client.SendAsync(request);
        return response.StatusCode;
    }

    [Fact]
    public async Task MobileRefreshRotatesTokensAndPersistsOnlyDigests()
    {
        using var web = Client();
        var enrolled = await Enroll(web);
        using var mobile = Client(false);
        var first = await IssueMobileTokens(mobile, enrolled.Key);
        _clock.Now = _clock.Now.AddMinutes(5);
        using var response = await RefreshMobile(mobile, first.RefreshToken);
        var second = await ReadTokens(response);
        Assert.NotEqual(first.RefreshToken, second.RefreshToken);
        Assert.NotEqual(first.AccessToken, second.AccessToken);
        Assert.Equal(first.RefreshExpiresAtUtc, second.RefreshExpiresAtUtc);
        Assert.Equal(_clock.Now.AddMinutes(15), second.ExpiresAtUtc);
        Assert.Equal(HttpStatusCode.Unauthorized, await MobileAccess(mobile, first.AccessToken));
        Assert.Equal(HttpStatusCode.OK, await MobileAccess(mobile, second.AccessToken));
        // The two credential types cannot be substituted.
        Assert.Equal(HttpStatusCode.Unauthorized, await MobileAccess(mobile, second.RefreshToken));
        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshMobile(mobile, second.AccessToken)).StatusCode);
        await using var db = _database.CreateContext();
        var session = await db.Set<MobileStaffSession>().SingleAsync();
        var history = await db.Set<MobileStaffRefreshToken>().ToListAsync();
        Assert.Equal(2, history.Count);
        var consumed = Assert.Single(history, x => x.ConsumedAtUtc is not null);
        var current = Assert.Single(history, x => x.ConsumedAtUtc is null);
        Assert.Equal(Digest(first.RefreshToken), consumed.TokenHash);
        Assert.Equal(Digest(second.RefreshToken), current.TokenHash);
        Assert.Equal(session.Id, current.SessionId);
        Assert.Equal(session.CreatedAtUtc.AddDays(7), session.RefreshExpiresAtUtc);
        var persisted = JsonSerializer.Serialize(session) + JsonSerializer.Serialize(history);
        foreach (var token in new[] { first.RefreshToken, first.AccessToken, second.RefreshToken, second.AccessToken })
        {
            Assert.Equal(32, Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlDecode(token).Length);
            Assert.DoesNotContain(token, persisted);
        }
    }

    private static string Digest(string value) => Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(value)));

    [Fact]
    public async Task MobileRefreshWorksAfterAccessExpiresWithoutAccessHeader()
    {
        using var web = Client();
        var enrolled = await Enroll(web);
        using var mobile = Client(false);
        var first = await IssueMobileTokens(mobile, enrolled.Key);
        _clock.Now = _clock.Now.AddMinutes(16);
        Assert.Equal(HttpStatusCode.Unauthorized, await MobileAccess(mobile, first.AccessToken));
        using var response = await RefreshMobile(mobile, first.RefreshToken);
        var second = await ReadTokens(response);
        Assert.Equal(HttpStatusCode.OK, await MobileAccess(mobile, second.AccessToken));
        Assert.Equal(first.RefreshExpiresAtUtc, second.RefreshExpiresAtUtc);
    }

    [Fact]
    public async Task MobileRefreshReplayOfAncestorRevokesOnlyItsFamilyAndAuditsWithoutSecrets()
    {
        using var web = Client();
        var enrolled = await Enroll(web);
        using var mobile = Client(false);
        var first = await IssueMobileTokens(mobile, enrolled.Key);
        using var response2 = await RefreshMobile(mobile, first.RefreshToken);
        var second = await ReadTokens(response2);
        using var response3 = await RefreshMobile(mobile, second.RefreshToken);
        var third = await ReadTokens(response3);
        var independent = await IssueMobileTokens(mobile, enrolled.Key);
        using var replay = await RefreshMobile(mobile, first.RefreshToken);
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
        Assert.True(replay.Headers.CacheControl?.NoStore);
        Assert.Contains("invalid_credentials", await Body(replay));
        Assert.Equal(HttpStatusCode.Unauthorized, await MobileAccess(mobile, third.AccessToken));
        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshMobile(mobile, third.RefreshToken)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, await MobileAccess(mobile, independent.AccessToken));
        Assert.Equal(HttpStatusCode.OK, (await web.GetAsync("/api/staff/auth/session")).StatusCode);
        await using var db = _database.CreateContext();
        Assert.Equal(1, await db.Set<MobileStaffSession>().CountAsync(x => x.RevokedAtUtc != null));
        var audit = await db.AuditEvents.Where(x => x.ActionCode.StartsWith("staff.mobile.")).ToListAsync();
        Assert.Equal(2, audit.Count(x => x.ActionCode == "staff.mobile.session.refreshed"));
        Assert.Single(audit, x => x.ActionCode == "staff.mobile.replay.revoked");
        Assert.All(audit, x => Assert.Empty(x.Metadata));
        var output = string.Join('\n', _logs.Messages) + JsonSerializer.Serialize(audit) + await Body(replay);
        foreach (var token in new[] { first, second, third, independent })
        {
            Assert.DoesNotContain(token.AccessToken, output);
            Assert.DoesNotContain(token.RefreshToken, output);
        }
        Assert.DoesNotContain(Password, output);
        Assert.DoesNotContain(enrolled.Key, output);
    }

    [Fact]
    public async Task MobileRefreshConcurrentRaceHasOneWinnerThenRevokesFamily()
    {
        using var web = Client();
        var enrolled = await Enroll(web);
        using var firstClient = Client(false);
        using var secondClient = Client(false);
        var first = await IssueMobileTokens(firstClient, enrolled.Key);
        var responses = await Task.WhenAll(RefreshMobile(firstClient, first.RefreshToken), RefreshMobile(secondClient, first.RefreshToken));
        var winner = await ReadTokens(Assert.Single(responses, x => x.StatusCode == HttpStatusCode.OK));
        Assert.Single(responses, x => x.StatusCode == HttpStatusCode.Unauthorized);
        Assert.Equal(HttpStatusCode.Unauthorized, await MobileAccess(firstClient, winner.AccessToken));
        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshMobile(firstClient, winner.RefreshToken)).StatusCode);
        await using var db = _database.CreateContext();
        Assert.NotNull((await db.Set<MobileStaffSession>().SingleAsync()).RevokedAtUtc);
        Assert.Equal(2, await db.Set<MobileStaffRefreshToken>().CountAsync());
        Assert.Equal(1, await db.Set<MobileStaffRefreshToken>().CountAsync(x => x.ConsumedAtUtc != null));
        foreach (var response in responses) response.Dispose();
    }

    [Fact]
    public async Task MobileRefreshLogoutRevokesAccessAndRefresh()
    {
        using var web = Client();
        var enrolled = await Enroll(web);
        using var mobile = Client(false);
        var first = await IssueMobileTokens(mobile, enrolled.Key);
        using var refreshed = await RefreshMobile(mobile, first.RefreshToken);
        var second = await ReadTokens(refreshed);
        mobile.DefaultRequestHeaders.Authorization = new("Bearer", second.AccessToken);
        Assert.Equal(HttpStatusCode.NoContent, (await mobile.PostAsync(MobileRoute + "logout", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, await MobileAccess(mobile, second.AccessToken));
        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshMobile(mobile, second.RefreshToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshMobile(mobile, first.RefreshToken)).StatusCode);
    }

    [Theory]
    [InlineData("disable")]
    [InlineData("revoke")]
    [InlineData("reset-password")]
    [InlineData("reset-mfa")]
    [InlineData("stamp")]
    [InlineData("role")]
    [InlineData("mfa-state")]
    [InlineData("lockout")]
    public async Task MobileRefreshRevalidatesIdentityAndNeverRotatesOnFailure(string operation)
    {
        using var web = Client();
        var enrolled = await Enroll(web);
        using var mobile = Client(false);
        var first = await IssueMobileTokens(mobile, enrolled.Key);
        using (var scope = _factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<StaffUser>>();
            var user = (await users.FindByIdAsync(_id))!;
            var stamp = user.SecurityStamp;
            switch (operation)
            {
                case "stamp": Assert.True((await users.UpdateSecurityStampAsync(user)).Succeeded); break;
                case "role":
                    Assert.True((await users.AddToRoleAsync(user, "Receptionist")).Succeeded);
                    Assert.True((await users.RemoveFromRoleAsync(user, "Doctor")).Succeeded);
                    Assert.Equal(stamp, user.SecurityStamp);
                    break;
                case "mfa-state":
                    // Exercise persisted MFA revalidation independently from stamp revocation.
                    var context = scope.ServiceProvider.GetRequiredService<ClinicDbContext>();
                    await context.Users.Where(x => x.Id == _id).ExecuteUpdateAsync(s => s.SetProperty(x => x.TwoFactorEnabled, false));
                    break;
                case "lockout": Assert.True((await users.SetLockoutEndDateAsync(user, DateTimeOffset.UtcNow.AddHours(1))).Succeeded); break;
                default: await scope.ServiceProvider.GetRequiredService<StaffAdministration>().ChangeAsync(_id, operation, "New-Synthetic-Password-94!"); break;
            }
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshMobile(mobile, first.RefreshToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, await MobileAccess(mobile, first.AccessToken));
        await using var db = _database.CreateContext();
        var original = await db.Set<MobileStaffRefreshToken>().SingleAsync();
        Assert.Null(original.ConsumedAtUtc);
        Assert.Equal(Digest(first.RefreshToken), original.TokenHash);
    }

    [Fact]
    public async Task MobileRefreshAbsoluteDeadlineNeverSlidesAndCapsAccessExpiry()
    {
        using var web = Client();
        var enrolled = await Enroll(web);
        using var mobile = Client(false);
        var first = await IssueMobileTokens(mobile, enrolled.Key);
        _clock.Now = first.RefreshExpiresAtUtc.AddMinutes(-1);
        using var response = await RefreshMobile(mobile, first.RefreshToken);
        var second = await ReadTokens(response);
        Assert.Equal(first.RefreshExpiresAtUtc, second.RefreshExpiresAtUtc);
        Assert.Equal(first.RefreshExpiresAtUtc, second.ExpiresAtUtc);
        Assert.Equal(60, (await Json(response)).GetProperty("expiresIn").GetInt32());
        _clock.Now = first.RefreshExpiresAtUtc;
        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshMobile(mobile, second.RefreshToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, await MobileAccess(mobile, second.AccessToken));
        await using var db = _database.CreateContext();
        Assert.Equal(first.RefreshExpiresAtUtc, (await db.Set<MobileStaffSession>().SingleAsync()).RefreshExpiresAtUtc);
    }

    [Fact]
    public async Task MobileRefreshUnknownAndMalformedValuesDoNotRevokeSessionsOrEchoSecrets()
    {
        using var web = Client();
        var enrolled = await Enroll(web);
        using var mobile = Client(false);
        var first = await IssueMobileTokens(mobile, enrolled.Key);
        var unknown = Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        foreach (var value in new[] { unknown, "malformed-secret", first.RefreshToken + "extra" })
        {
            using var response = await RefreshMobile(mobile, value);
            Assert.True(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.BadRequest);
            Assert.DoesNotContain(value, await Body(response));
            Assert.DoesNotContain(value, string.Join('\n', _logs.Messages));
        }
        // JSON is mandatory. No query-string or cookie fallback exists.
        Assert.Equal(HttpStatusCode.BadRequest, (await mobile.PostAsJsonAsync(MobileRoute + "refresh", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, await MobileAccess(mobile, first.AccessToken));
        using var valid = await RefreshMobile(mobile, first.RefreshToken);
        await ReadTokens(valid);
    }
}
