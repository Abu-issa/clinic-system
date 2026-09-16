using System.Net;
using System.Net.Http.Json;
using Clinic.Domain.Enums;
using Clinic.Infrastructure.Persistence;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Clinic.IntegrationTests.Api;

public sealed partial class StaffIdentityHttpTests
{
    [Fact]
    public async Task SecuritySuccessEventsContainOnlyKnownStaffIdentityAndNoSecrets()
    {
        using var first = Client();
        var enrolled = await Enroll(first);
        using var second = Client();
        Assert.Equal(HttpStatusCode.OK, (await Login(second)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await second.PostAsJsonAsync("/api/staff/auth/totp", new { code = Totp(enrolled.Key) })).StatusCode);
        await Csrf(second);
        Assert.Equal(HttpStatusCode.OK, (await second.PostAsync("/api/staff/auth/logout", null)).StatusCode);
        using var recovery = Client();
        Assert.Equal(HttpStatusCode.OK, (await Login(recovery)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await recovery.PostAsJsonAsync("/api/staff/auth/recovery", new { code = enrolled.Codes[0] })).StatusCode);
        await Csrf(recovery);
        Assert.Equal(HttpStatusCode.OK, (await recovery.PostAsync("/api/staff/auth/revoke-sessions", null)).StatusCode);
        await using var db = _database.CreateContext();
        var actions = new[] { "staff.password.accepted", "staff.enrollment.setup", "staff.enrollment.completed",
            "staff.mfa.completed", "staff.recovery.login", "staff.logout", "staff.sessions-revoke" };
        var events = await db.AuditEvents.Where(x => x.ActorStaffId == _id).ToListAsync();
        Assert.Equal(9, events.Count); // Three password acceptances, six distinct following actions.
        Assert.Equal(actions.Order(), events.Select(x => x.ActionCode).Distinct().Order());
        Assert.All(events, e =>
        {
            Assert.Equal("staff-account", e.ResourceType);
            Assert.Equal(_id, e.ResourceId);
            Assert.Null(e.PatientId);
            Assert.Equal(AuditOutcome.Succeeded, e.Outcome);
            Assert.Empty(e.Metadata);
            Assert.False(string.IsNullOrWhiteSpace(e.TraceId));
        });
        var serialized = System.Text.Json.JsonSerializer.Serialize(events);
        foreach (var secret in enrolled.Codes.Append(enrolled.Key).Append(Password).Append(Name))
            Assert.DoesNotContain(secret, serialized);
    }

    [Fact]
    public async Task SecurityAuditOutageDoesNotBlockAuthenticationOrLogout()
    {
        var failure = new AccessAuditHttpTests.FailAuditInsert { Enabled = true };
        using var original = _factory;
        _factory = original.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddDbContext<ClinicDbContext>(options => options.AddInterceptors(failure))));
        using var client = Client();
        await Enroll(client);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/staff/auth/session")).StatusCode);
        await Csrf(client);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/staff/auth/logout", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/staff/auth/session")).StatusCode);
        Assert.Equal(4, failure.Attempts);
        await using var db = _database.CreateContext();
        Assert.False(await db.AuditEvents.AnyAsync(x => x.ActorStaffId == _id));
        Assert.Contains(_logs.Messages, x => x.Contains("Security audit persistence failed."));
    }

    [Fact]
    public async Task FailedCredentialsRemainNeutralAndDoNotAmplifyAuditWrites()
    {
        var failure = new AccessAuditHttpTests.FailAuditInsert { Enabled = true };
        using var original = _factory;
        _factory = original.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddDbContext<ClinicDbContext>(options => options.AddInterceptors(failure))));
        using var client = Client();
        await Csrf(client);
        using var unknown = await client.PostAsJsonAsync("/api/staff/auth/login", new { userName = "missing-private-name", password = "wrong-private-password" });
        using var known = await Login(client, "wrong-private-password");
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, known.StatusCode);
        Assert.Equal((await Json(unknown)).GetProperty("code").GetString(), (await Json(known)).GetProperty("code").GetString());
        for (var i = 0; i < 4; i++) Assert.Equal(HttpStatusCode.Unauthorized, (await Login(client, "wrong-private-password")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Login(client)).StatusCode);
        Assert.Equal(0, failure.Attempts);
        await using var db = _database.CreateContext();
        Assert.False(await db.AuditEvents.AnyAsync(x => x.ActorStaffId == _id));
        Assert.True((await db.Users.SingleAsync(x => x.Id == _id)).LockoutEnd > DateTimeOffset.UtcNow);
    }
}
