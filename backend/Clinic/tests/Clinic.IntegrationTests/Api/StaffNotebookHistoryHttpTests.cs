using System.Net;
using Clinic.Application.Audit;
using Clinic.Infrastructure.Authentication;
using Clinic.Infrastructure.Persistence;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Clinic.IntegrationTests.Api;

public sealed partial class StaffIdentityHttpTests
{
    private static string HistoryRoute(Guid patient, Guid page) =>
        $"/api/staff/patients/{patient}/notebook/pages/{page}/revisions";

    [Fact]
    public async Task NotebookHistoryMetadataIsOrderedPaginatedSafeAndAudited()
    {
        var (patients, _, _) = await NotebookPatients("Doctor", ["notebook.read", "notebook.write"]);
        using var web = await NotebookSession();
        var page = await CreateNotebookPageOk(web, patients[0].Id, new { title = "Private note" });
        var id = page.GetProperty("id").GetGuid();
        using var upload = await UploadNotebook(web, patients[0].Id, id, page.GetProperty("rowVersion").GetString()!, "draft");
        var saved = await Json(upload);
        using var finalized = await FinalizeNotebook(web, patients[0].Id, id, saved.GetProperty("rowVersion").GetString()!);
        using var amended = await UploadNotebook(web, patients[0].Id, id, (await Json(finalized)).GetProperty("rowVersion").GetString()!, "amend", amendment: true);
        Assert.Equal(HttpStatusCode.Created, amended.StatusCode);
        var route = HistoryRoute(patients[0].Id, id);
        using var first = await web.GetAsync(route + "?page=1&pageSize=2");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.True(first.Headers.CacheControl?.NoStore);
        var json = await Json(first);
        Assert.Equal(patients[0].Id, json.GetProperty("patientId").GetGuid());
        Assert.Equal(id, json.GetProperty("pageId").GetGuid());
        Assert.Equal(3, json.GetProperty("currentRevisionNumber").GetInt64());
        Assert.True(json.GetProperty("hasMore").GetBoolean());
        var rows = json.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(new long[] { 3, 2 }, rows.Select(r => r.GetProperty("revisionNumber").GetInt64()));
        Assert.Equal(new[] { "Amendment", "Revision" }, rows.Select(r => r.GetProperty("kind").GetString()));
        Assert.All(rows, r => {
            Assert.Equal(_id, r.GetProperty("authorStaffId").GetString());
            Assert.True(r.GetProperty("hasPayload").GetBoolean());
            Assert.Equal(_clock.Now, r.GetProperty("createdAtUtc").GetDateTimeOffset());
            Assert.Equal(new[] { "authorStaffId", "createdAtUtc", "hasPayload", "kind", "revisionNumber" }, r.EnumerateObject().Select(p => p.Name).Order());
        });
        using var second = await web.GetAsync(route + "?page=2&pageSize=2");
        var next = await Json(second);
        Assert.False(next.GetProperty("hasMore").GetBoolean());
        var created = next.GetProperty("items")[0];
        Assert.Equal("Created", created.GetProperty("kind").GetString());
        Assert.False(created.GetProperty("hasPayload").GetBoolean());
        using var payload = await web.GetAsync(PayloadRoute(patients[0].Id, id));
        Assert.Equal(patients[0].Id.ToString(), payload.Headers.GetValues("X-Notebook-Patient").Single());
        Assert.Equal(id.ToString(), payload.Headers.GetValues("X-Notebook-Page").Single());
        Assert.Equal("2", payload.Headers.GetValues("X-Notebook-Revision").Single());
        await using var db = _database.CreateContext();
        var audits = await db.AuditEvents.Where(x => x.ActionCode == "notebook.revision.list").ToArrayAsync();
        Assert.Equal(2, audits.Length);
        Assert.All(audits, a => { Assert.Equal(patients[0].Id, a.PatientId); Assert.Equal(_id, a.ActorStaffId);
            Assert.Equal(id.ToString("N"), a.ResourceId); Assert.Empty(a.Metadata); });
    }

    [Fact]
    public async Task NotebookHistoryRejectsUnauthorizedScopeMismatchAndInvalidPagination()
    {
        var (patients, _, _) = await NotebookPatients("Doctor", ["notebook.read", "notebook.write"]);
        using var web = Client(); var enrolled = await Enroll(web);
        using var mobile = Client(false); await MobileComplete(mobile, enrolled.Key);
        var page = await CreateNotebookPageOk(mobile, patients[0].Id, new { title = "Note" });
        var id = page.GetProperty("id").GetGuid();
        var route = HistoryRoute(patients[0].Id, id);
        Assert.Equal(HttpStatusCode.OK, (await mobile.GetAsync(route)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await mobile.GetAsync(HistoryRoute(patients[1].Id, id))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await mobile.GetAsync(HistoryRoute(patients[2].Id, id))).StatusCode);
        using var anonymous = Client(false);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(route)).StatusCode);
        using var passwordOnly = Client();
        using var login = await Login(passwordOnly);
        Assert.Equal(HttpStatusCode.Unauthorized, (await passwordOnly.GetAsync(route)).StatusCode);
        web.DefaultRequestHeaders.Authorization = new("Bearer", "invalid");
        Assert.Equal(HttpStatusCode.Unauthorized, (await web.GetAsync(route)).StatusCode);
        foreach (var query in new[] { "page=0", "page=1000001", "pageSize=0", "pageSize=101" })
            Assert.Equal(HttpStatusCode.BadRequest, (await mobile.GetAsync(route + "?" + query)).StatusCode);
        using var services = _factory.Services.CreateScope();
        var db = services.ServiceProvider.GetRequiredService<ClinicDbContext>();
        db.Remove(await db.UserClaims.SingleAsync(x => x.UserId == _id && x.ClaimType == "permission" && x.ClaimValue == "notebook.read"));
        await db.SaveChangesAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await mobile.GetAsync(route)).StatusCode);
        Assert.Single(await db.AuditEvents.Where(x => x.ActionCode == "notebook.revision.list").ToArrayAsync());
    }

    [Fact]
    public async Task NotebookHistoryAuditFailurePreventsMetadataDisclosure()
    {
        var (patients, _, _) = await NotebookPatients("Doctor", ["notebook.read", "notebook.write"]);
        using var web = Client(); var enrolled = await Enroll(web);
        using var mobile = Client(false); var token = await MobileComplete(mobile, enrolled.Key);
        var page = await CreateNotebookPageOk(mobile, patients[0].Id, new { title = "Note" });
        using var host = _factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddScoped<IAccessAuditWriter, ContextAuditFailure>()));
        using var client = host.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        using var response = await client.GetAsync(HistoryRoute(patients[0].Id, page.GetProperty("id").GetGuid()));
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.DoesNotContain("authorStaffId", await Body(response));
        await using var db = _database.CreateContext();
        Assert.False(await db.AuditEvents.AnyAsync(x => x.ActionCode == "notebook.revision.list"));
    }
}
