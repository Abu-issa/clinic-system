using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Clinic.Domain.Entities;
using Clinic.Domain.Enums;
using Clinic.Infrastructure.Authentication;
using Clinic.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.TestHost;
using Clinic.Application.Audit;

namespace Clinic.IntegrationTests.Api;

// Doctor tablet notebook metadata endpoints (Phase 1A-4A): page create/list/read under the
// existing staff cookie/mobile sessions, notebook.* persisted permissions and fail-closed read auditing.
public sealed partial class StaffIdentityHttpTests
{
    [Fact]
    public async Task NotebookMobileAuthenticationEnforcesScopeAndDoesNotDowngradeInvalidBearer()
    {
        var (patients, _, _) = await NotebookPatients("Doctor", ["notebook.read", "notebook.write"]);
        using var web = Client();
        var enrolled = await Enroll(web);
        using var mobile = Client(false);
        await MobileComplete(mobile, enrolled.Key);
        var page = await CreateNotebookPageOk(mobile, patients[0].Id, new
        {
            title = "Mobile note", patientId = patients[2].Id, authorDoctorId = Guid.NewGuid(),
            currentRevisionNumber = 999, createdAtUtc = "2000-01-01T00:00:00Z"
        });
        Assert.Equal(patients[0].Id, page.GetProperty("patientId").GetGuid());
        Assert.Equal(_doctor, page.GetProperty("authorDoctorId").GetGuid());
        Assert.Equal(_clock.Now, page.GetProperty("createdAtUtc").GetDateTimeOffset());
        Assert.Equal(1, page.GetProperty("currentRevisionNumber").GetInt64());
        var route = string.Format(NotebookRoute, patients[0].Id);
        Assert.Equal(HttpStatusCode.OK, (await mobile.GetAsync(route)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await mobile.GetAsync(route + "/" + page.GetProperty("id").GetGuid())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await mobile.GetAsync(string.Format(NotebookRoute, patients[2].Id))).StatusCode);
        web.DefaultRequestHeaders.Authorization = new("Bearer", "invalid");
        Assert.Equal(HttpStatusCode.Unauthorized, (await web.GetAsync(route)).StatusCode);
        using var anonymous = Client(false);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(route)).StatusCode);
        using var services = _factory.Services.CreateScope();
        var db = services.ServiceProvider.GetRequiredService<ClinicDbContext>();
        db.Remove(await db.UserClaims.SingleAsync(x => x.UserId == _id && x.ClaimType == "permission" && x.ClaimValue == "notebook.read"));
        await db.SaveChangesAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await mobile.GetAsync(route)).StatusCode);
    }

    [Fact]
    public async Task NotebookReadAuditOutagePreventsDisclosureAndCreateAuditFailureRollsBack()
    {
        var (patients, _, _) = await NotebookPatients("Doctor", ["notebook.read", "notebook.write"]);
        using var web = Client(); var enrolled = await Enroll(web);
        using var mobile = Client(false); var token = await MobileComplete(mobile, enrolled.Key);
        var page = await CreateNotebookPageOk(mobile, patients[0].Id, new { title = "Sensitive notebook title" });
        var route = string.Format(NotebookRoute, patients[0].Id);
        using var readHost = _factory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
            s.AddScoped<IAccessAuditWriter, ContextAuditFailure>()));
        using var failingRead = readHost.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        failingRead.DefaultRequestHeaders.Authorization = new("Bearer", token);
        foreach (var path in new[] { route, route + "/" + page.GetProperty("id").GetGuid() })
        {
            using var response = await failingRead.GetAsync(path);
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
            Assert.DoesNotContain("Sensitive notebook title", await Body(response));
        }
        var failure = new AccessAuditHttpTests.FailAuditInsert { Enabled = true };
        using var writeHost = _factory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
            s.AddDbContext<ClinicDbContext>(o => o.AddInterceptors(failure))));
        using var failingWrite = writeHost.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        failingWrite.DefaultRequestHeaders.Authorization = new("Bearer", token);
        using var create = await CreateNotebookPage(failingWrite, patients[0].Id, new { title = "Must roll back" });
        Assert.Equal(HttpStatusCode.InternalServerError, create.StatusCode);
        await using var db = _database.CreateContext();
        Assert.Equal(1, await db.Set<NotebookPage>().CountAsync());
        Assert.Equal(1, await db.Set<NotebookRevision>().CountAsync());
        Assert.Equal(1, await db.AuditEvents.CountAsync(x => x.ActionCode == "notebook.page.create"));
        Assert.False(await db.AuditEvents.AnyAsync(x => x.ActionCode == "notebook.page.read" || x.ActionCode == "notebook.page.list"));
    }

    private const string NotebookRoute = "/api/staff/patients/{0}/notebook/pages";

    private async Task<(Patient[] Patients, Guid PatientVisit, Guid ForeignVisit)> NotebookPatients(
        string role, string[] permissions, int scopedCount = 2)
    {
        using var services = _factory.Services.CreateScope();
        var db = services.ServiceProvider.GetRequiredService<ClinicDbContext>();
        var patients = Enumerable.Range(0, 3).Select(i => new Patient($"NB-MRN-{i}", $"Notebook Patient {i}",
            "private-phone", new DateOnly(1991, 5, 6), $"PAPER-NB-{i}", "private-cover")).ToArray();
        db.AddRange(patients);
        var patientVisit = new Visit(patients[0].Id, _doctor, null, _clock.Now, _id, _clock.Now);
        var foreignVisit = new Visit(patients[2].Id, _doctor, null, _clock.Now, _id, _clock.Now);
        db.AddRange(patientVisit, foreignVisit);
        await db.SaveChangesAsync();
        await services.ServiceProvider.GetRequiredService<StaffAdministration>().ChangeAsync(_id, "set-grants",
            approvedRoles: [role], permissions: permissions,
            patientScopes: patients.Take(scopedCount).Select(p => p.Id).ToArray());
        return (patients, patientVisit.Id, foreignVisit.Id);
    }

    private async Task<HttpClient> NotebookSession()
    {
        var client = Client();
        await Enroll(client);
        return client;
    }

    private Task<HttpResponseMessage> CreateNotebookPage(HttpClient client, Guid patientId, object body) =>
        client.PostAsJsonAsync(string.Format(NotebookRoute, patientId), body);

    private async Task<JsonElement> CreateNotebookPageOk(HttpClient client, Guid patientId, object body)
    {
        using var response = await CreateNotebookPage(client, patientId, body);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await Json(response);
    }

    [Fact]
    public async Task DoctorCreatesPageReturnsMetadataRowVersionRevisionAndAudit()
    {
        var (patients, patientVisit, _) = await NotebookPatients("Doctor", ["notebook.read", "notebook.write"]);
        using var web = await NotebookSession();
        Token(web, "bogus-token");
        Assert.Equal(HttpStatusCode.BadRequest,
            (await CreateNotebookPage(web, patients[0].Id, new { title = "Round one" })).StatusCode);
        await Csrf(web);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await CreateNotebookPage(web, patients[0].Id, new { title = new string('x', 201) })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await CreateNotebookPage(web, patients[0].Id, new { title = "   " })).StatusCode);

        var page = await CreateNotebookPageOk(web, patients[0].Id, new
        {
            title = "  Round one  ",
            visitId = patientVisit,
            clientDraftId = " draft-1 ",
            originDeviceId = " tablet-7 "
        });
        Assert.Equal(patients[0].Id, page.GetProperty("patientId").GetGuid());
        Assert.Equal(patientVisit, page.GetProperty("visitId").GetGuid());
        Assert.Equal(_doctor, page.GetProperty("authorDoctorId").GetGuid());
        Assert.Equal("Round one", page.GetProperty("title").GetString());
        Assert.Equal(1, page.GetProperty("currentRevisionNumber").GetInt64());
        Assert.Equal(JsonValueKind.Null, page.GetProperty("finalizedAtUtc").ValueKind);
        Assert.Equal(JsonValueKind.Null, page.GetProperty("finalizedByDoctorId").ValueKind);
        Assert.Equal(8, Convert.FromBase64String(page.GetProperty("rowVersion").GetString()!).Length);
        var pageId = page.GetProperty("id").GetGuid();

        await using var db = _database.CreateContext();
        var revision = await db.Set<NotebookRevision>().SingleAsync(x => x.PageId == pageId);
        Assert.Equal(1, revision.RevisionNumber);
        Assert.Equal(NotebookRevisionKind.Created, revision.Kind);
        Assert.Equal(_id, revision.AuthorStaffId);
        Assert.Equal("draft-1", revision.ClientDraftId);
        Assert.Equal("tablet-7", revision.OriginDeviceId);

        var created = await db.AuditEvents.SingleAsync(x => x.ActionCode == "notebook.page.create");
        Assert.Equal(_id, created.ActorStaffId);
        Assert.Equal("notebook-page", created.ResourceType);
        Assert.Equal(pageId.ToString("N"), created.ResourceId);
        Assert.Equal(patients[0].Id, created.PatientId);
        Assert.Equal(AuditOutcome.Succeeded, created.Outcome);
        Assert.Empty(created.Metadata);
    }

    [Fact]
    public async Task DoctorReadsPageReturnsRowVersionAndAuditsOnlyAuthorizedReads()
    {
        var (patients, _, _) = await NotebookPatients("Doctor", ["notebook.read", "notebook.write"]);
        using var web = await NotebookSession();
        var page = await CreateNotebookPageOk(web, patients[0].Id, new { title = "History note" });
        var pageId = page.GetProperty("id").GetGuid();

        using var read = await web.GetAsync(string.Format(NotebookRoute, patients[0].Id) + $"/{pageId}");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        var body = await Json(read);
        Assert.Equal(pageId, body.GetProperty("id").GetGuid());
        Assert.Equal(page.GetProperty("rowVersion").GetString(), body.GetProperty("rowVersion").GetString());
        Assert.Equal(1, body.GetProperty("currentRevisionNumber").GetInt64());

        Assert.Equal(HttpStatusCode.NotFound,
            (await web.GetAsync(string.Format(NotebookRoute, patients[0].Id) + $"/{Guid.NewGuid()}")).StatusCode);
        // Same page ID under another in-scope patient resolves as not found: the route patient wins.
        Assert.Equal(HttpStatusCode.NotFound,
            (await web.GetAsync(string.Format(NotebookRoute, patients[1].Id) + $"/{pageId}")).StatusCode);

        await using var db = _database.CreateContext();
        var reads = await db.AuditEvents.Where(x => x.ActionCode == "notebook.page.read").ToListAsync();
        Assert.Single(reads);
        Assert.Equal(_id, reads[0].ActorStaffId);
        Assert.Equal("notebook-page", reads[0].ResourceType);
        Assert.Equal(pageId.ToString("N"), reads[0].ResourceId);
        Assert.Equal(patients[0].Id, reads[0].PatientId);
    }

    [Fact]
    public async Task DoctorListIsBoundedAuditedAndPaginated()
    {
        var (patients, _, _) = await NotebookPatients("Doctor", ["notebook.read", "notebook.write"]);
        using var web = await NotebookSession();
        for (var i = 0; i < 3; i++)
        {
            await CreateNotebookPageOk(web, patients[0].Id, new { title = $"Note {i}" });
            _clock.Now = _clock.Now.AddSeconds(1);
        }
        var route = string.Format(NotebookRoute, patients[0].Id);

        foreach (var (page, pageSize) in new[] { (0, 10), (101, 10), (1, 0), (1, 21), (-1, 5) })
            Assert.Equal(HttpStatusCode.BadRequest,
                (await web.GetAsync($"{route}?page={page}&pageSize={pageSize}")).StatusCode);

        await using (var db = _database.CreateContext())
            Assert.Empty(await db.AuditEvents.Where(x => x.ActionCode == "notebook.page.list").ToListAsync());

        using var first = await web.GetAsync($"{route}?page=1&pageSize=2");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.True(first.Headers.CacheControl?.NoStore);
        var firstBody = await Json(first);
        Assert.True(firstBody.GetProperty("hasMore").GetBoolean());
        Assert.Equal(2, firstBody.GetProperty("items").GetArrayLength());
        Assert.Equal("Note 2", firstBody.GetProperty("items")[0].GetProperty("title").GetString());
        Assert.Equal(1, firstBody.GetProperty("items")[0].GetProperty("currentRevisionNumber").GetInt64());

        using var second = await web.GetAsync($"{route}?page=2&pageSize=2");
        var secondBody = await Json(second);
        Assert.False(secondBody.GetProperty("hasMore").GetBoolean());
        Assert.Equal(1, secondBody.GetProperty("items").GetArrayLength());
        Assert.Equal("Note 0", secondBody.GetProperty("items")[0].GetProperty("title").GetString());

        await using var verify = _database.CreateContext();
        Assert.Equal(2, (await verify.AuditEvents
            .Where(x => x.ActionCode == "notebook.page.list").ToListAsync()).Count);
        Assert.Equal(3, (await verify.AuditEvents
            .Where(x => x.ActionCode == "notebook.page.create").ToListAsync()).Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DoctorAssistantIsReadOnlyEvenWithWritePermissionGranted(bool grantWrite)
    {
        var permissions = grantWrite ? ["notebook.read", "notebook.write"] : new[] { "notebook.read" };
        var (patients, _, _) = await NotebookPatients("DoctorAssistant", permissions);
        using var web = await NotebookSession();

        Assert.Equal(HttpStatusCode.Forbidden,
            (await CreateNotebookPage(web, patients[0].Id, new { title = "Assistant note" })).StatusCode);
        using var read = await web.GetAsync(string.Format(NotebookRoute, patients[0].Id) + "?pageSize=5");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.True(read.Headers.CacheControl?.NoStore);
        Assert.False((await Json(read)).GetProperty("hasMore").GetBoolean());

        Guid seededPageId;
        await using (var seed = _database.CreateContext())
        {
            var seeded = new NotebookPage(patients[0].Id, null, _doctor, "Existing doctor note", _id, _clock.Now, null, null);
            seededPageId = seeded.Id;
            seed.Add(seeded);
            await seed.SaveChangesAsync();
        }
        Assert.Equal(HttpStatusCode.OK,
            (await web.GetAsync(string.Format(NotebookRoute, patients[0].Id) + $"/{seededPageId}")).StatusCode);

        await using var db = _database.CreateContext();
        Assert.Empty(await db.AuditEvents.Where(x => x.ActionCode == "notebook.page.create").ToListAsync());
        // The assistant's own authorized list read is audited; nothing else is.
        Assert.Single(await db.AuditEvents.Where(x => x.ActionCode == "notebook.page.list").ToListAsync());
        Assert.All(await db.AuditEvents.Where(x => x.ActionCode.StartsWith("notebook.")).ToListAsync(),
            x => Assert.Equal(_id, x.ActorStaffId));
    }

    [Fact]
    public async Task ReceptionistIsDeniedEvenWithNotebookGrants()
    {
        var (patients, _, _) = await NotebookPatients("Receptionist", ["notebook.read", "notebook.write"]);
        using var web = await NotebookSession();

        Assert.Equal(HttpStatusCode.Forbidden,
            (await CreateNotebookPage(web, patients[0].Id, new { title = "Front desk" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await web.GetAsync(string.Format(NotebookRoute, patients[0].Id))).StatusCode);

        await using var db = _database.CreateContext();
        Assert.Empty(await db.AuditEvents.Where(x => x.ActionCode.StartsWith("notebook.")).ToListAsync());
    }

    [Fact]
    public async Task OutOfScopePatientIsForbiddenForEveryNotebookOperation()
    {
        var (patients, _, _) = await NotebookPatients("Doctor", ["notebook.read", "notebook.write"], scopedCount: 2);
        using var web = await NotebookSession();
        await CreateNotebookPageOk(web, patients[0].Id, new { title = "In scope" });

        Assert.Equal(HttpStatusCode.Forbidden,
            (await CreateNotebookPage(web, patients[2].Id, new { title = "Out of scope" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await web.GetAsync(string.Format(NotebookRoute, patients[2].Id))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await web.GetAsync(string.Format(NotebookRoute, patients[2].Id) + $"/{Guid.NewGuid()}")).StatusCode);

        await using var db = _database.CreateContext();
        Assert.Empty(await db.AuditEvents.Where(x => x.PatientId == patients[2].Id &&
            x.ActionCode.StartsWith("notebook.")).ToListAsync());
    }

    [Fact]
    public async Task ForeignOrUnknownVisitIsRejected()
    {
        var (patients, _, foreignVisit) = await NotebookPatients("Doctor", ["notebook.read", "notebook.write"]);
        using var web = await NotebookSession();

        using var foreign = await CreateNotebookPage(web, patients[0].Id, new { title = "Wrong visit", visitId = foreignVisit });
        Assert.Equal(HttpStatusCode.Conflict, foreign.StatusCode);
        Assert.Equal("visit_mismatch", (await Json(foreign)).GetProperty("code").GetString());
        using var unknown = await CreateNotebookPage(web, patients[0].Id, new { title = "Missing visit", visitId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal("visit_not_found", (await Json(unknown)).GetProperty("code").GetString());

        await using var db = _database.CreateContext();
        Assert.Empty(await db.AuditEvents.Where(x => x.ActionCode == "notebook.page.create").ToListAsync());
        Assert.Empty(await db.Set<NotebookPage>().ToListAsync());
    }

    [Fact]
    public async Task RevisionNumberAndClientDraftIdAreUniquePerPage()
    {
        var (patients, _, _) = await NotebookPatients("Doctor", ["notebook.read", "notebook.write"]);
        using var web = await NotebookSession();
        var page = await CreateNotebookPageOk(web, patients[0].Id,
            new { title = "Unique", clientDraftId = "draft-1" });
        var pageId = page.GetProperty("id").GetGuid();

        await using var db = _database.CreateContext();
        db.Add(new NotebookRevision(pageId, 1, _id, _clock.Now, NotebookRevisionKind.Created, null, null));
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();
        db.Add(new NotebookRevision(pageId, 2, _id, _clock.Now, NotebookRevisionKind.Created, "draft-1", null));
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();
        // The filtered unique index leaves null draft IDs unlimited.
        db.Add(new NotebookRevision(pageId, 2, _id, _clock.Now, NotebookRevisionKind.Created, null, null));
        await db.SaveChangesAsync();
        Assert.Equal(2, await db.Set<NotebookRevision>().CountAsync(x => x.PageId == pageId));
        db.ChangeTracker.Clear();
        db.Add(new NotebookRevision(pageId, 3, "missing-staff", _clock.Now, NotebookRevisionKind.Created, null, null));
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();
        db.Remove(await db.Set<NotebookPage>().SingleAsync(x => x.Id == pageId));
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();
        // The same draft key is permitted on a different page.
        var another = new NotebookPage(patients[0].Id, null, _doctor, "Other", _id, _clock.Now, "draft-1", null);
        db.Add(another);
        await db.SaveChangesAsync();
    }
}
