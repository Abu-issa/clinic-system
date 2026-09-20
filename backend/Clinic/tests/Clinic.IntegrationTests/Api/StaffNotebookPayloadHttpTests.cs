using System.Net;
using System.Text;
using System.Text.Json;
using Clinic.Application.Audit;
using Clinic.Application.Storage;
using Clinic.Domain.Entities;
using Clinic.Infrastructure.Persistence;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Clinic.IntegrationTests.Api;

public sealed partial class StaffIdentityHttpTests
{
    private static byte[] NotebookBytes(Guid patient, Guid page) =>
        JsonSerializer.SerializeToUtf8Bytes(new { formatVersion = 1, patientId = patient, pageId = page });

    private static async Task<HttpResponseMessage> UploadNotebook(HttpClient client, Guid patient, Guid page,
        string version, string draft, byte[]? bytes = null, string device = "tablet", bool amendment = false)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(version), "expectedRowVersion");
        form.Add(new StringContent(draft), "clientDraftId");
        form.Add(new StringContent(device), "originDeviceId");
        form.Add(new ByteArrayContent(bytes ?? NotebookBytes(patient, page)), "payload", "../../ignored.json");
        return await client.PostAsync($"/api/staff/patients/{patient}/notebook/pages/{page}/{(amendment ? "amendments" : "revisions")}", form);
    }

    private static string PayloadRoute(Guid patient, Guid page, long revision = 2) =>
        $"/api/staff/patients/{patient}/notebook/pages/{page}/revisions/{revision}/payload";

    [Fact]
    public async Task NotebookPayloadSavesImmutableHistoryAndRetriesWithoutDuplicates()
    {
        var (patients, _, _) = await NotebookPatients("Doctor", ["notebook.read", "notebook.write"]);
        using var web = await NotebookSession();
        var page = await CreateNotebookPageOk(web, patients[0].Id, new { title = "Private title" });
        var id = page.GetProperty("id").GetGuid(); var version = page.GetProperty("rowVersion").GetString()!;
        using var first = await UploadNotebook(web, patients[0].Id, id, version, "draft-a");
        Assert.True(first.StatusCode == HttpStatusCode.Created, await Body(first));
        var firstBody = await Json(first);
        Assert.Equal(2, firstBody.GetProperty("revisionNumber").GetInt64());
        Assert.NotEqual(version, firstBody.GetProperty("rowVersion").GetString());
        using var retry = await UploadNotebook(web, patients[0].Id, id, version, "draft-a");
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.True((await Json(retry)).GetProperty("replayed").GetBoolean());
        _clock.Now = _clock.Now.AddMinutes(1);
        using var second = await UploadNotebook(web, patients[0].Id, id, firstBody.GetProperty("rowVersion").GetString()!, "draft-b");
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        Assert.Equal(3, (await Json(second)).GetProperty("revisionNumber").GetInt64());
        using var oldRetry = await UploadNotebook(web, patients[0].Id, id, version, "draft-a");
        Assert.Equal(HttpStatusCode.OK, oldRetry.StatusCode);
        using var read = await web.GetAsync(PayloadRoute(patients[0].Id, id));
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.True(read.Headers.CacheControl?.NoStore);
        Assert.Equal(NotebookBytes(patients[0].Id, id), await read.Content.ReadAsByteArrayAsync());
        Assert.DoesNotContain("clinic-files", await Body(first));
        await using var db = _database.CreateContext();
        var revisions = await db.Set<NotebookRevision>().Where(x => x.PageId == id).OrderBy(x => x.RevisionNumber).ToListAsync();
        Assert.Equal(3, revisions.Count);
        Assert.Equal(firstBody.GetProperty("revisionId").GetGuid(), revisions[1].Id);
        Assert.Equal(_id, revisions[1].AuthorStaffId);
        Assert.True(revisions[1].CreatedAtUtc < revisions[2].CreatedAtUtc);
        Assert.Equal(2, await db.Set<StoredFile>().CountAsync());
        Assert.Equal(2, await db.AuditEvents.CountAsync(x => x.ActionCode == "notebook.revision.save"));
        Assert.Single(await db.AuditEvents.Where(x => x.ActionCode == "notebook.revision.read").ToArrayAsync());
        Assert.All(await db.AuditEvents.Where(x => x.ActionCode.StartsWith("notebook.revision.")).ToArrayAsync(), x => Assert.Empty(x.Metadata));
        db.Entry(revisions[1]).Property(x => x.OriginDeviceId).CurrentValue = "tampered";
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task NotebookPayloadRejectsStaleConflictingAndMismatchedRequests()
    {
        var (patients, _, _) = await NotebookPatients("Doctor", ["notebook.read", "notebook.write"]);
        using var web = await NotebookSession();
        var page = await CreateNotebookPageOk(web, patients[0].Id, new { title = "Note" });
        var id = page.GetProperty("id").GetGuid(); var version = page.GetProperty("rowVersion").GetString()!;
        Assert.Equal(HttpStatusCode.NotFound, (await UploadNotebook(web, patients[1].Id, id, version, "x")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await UploadNotebook(web, patients[2].Id, id, version, "x")).StatusCode);
        foreach (var bytes in new[] { NotebookBytes(patients[1].Id, id), NotebookBytes(patients[0].Id, Guid.NewGuid()), "{}"u8.ToArray(), "not-json"u8.ToArray() })
            Assert.Equal(HttpStatusCode.BadRequest, (await UploadNotebook(web, patients[0].Id, id, version, "x", bytes)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await UploadNotebook(web, patients[0].Id, id, "bad", "x")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await UploadNotebook(web, patients[0].Id, id, version, " ")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await UploadNotebook(web, patients[0].Id, id, version, "x")).StatusCode);
        using var stale = await UploadNotebook(web, patients[0].Id, id, version, "new");
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("page_changed", (await Json(stale)).GetProperty("code").GetString());
        using var conflict = await UploadNotebook(web, patients[0].Id, id, version, "x", Encoding.UTF8.GetBytes(" " + Encoding.UTF8.GetString(NotebookBytes(patients[0].Id, id))));
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal("draft_conflict", (await Json(conflict)).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await UploadNotebook(web, patients[0].Id, id, version, "x", device: "other")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await web.GetAsync(PayloadRoute(patients[1].Id, id))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await web.GetAsync(PayloadRoute(patients[2].Id, id))).StatusCode);
        using var anonymous = Client(false);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(PayloadRoute(patients[0].Id, id))).StatusCode);
        await using var db = _database.CreateContext();
        Assert.Equal(2, await db.Set<NotebookRevision>().CountAsync());
        Assert.Equal(1, await db.Set<StoredFile>().CountAsync());
        var root = _factory.Services.GetRequiredService<IOptions<FileStorageOptions>>().Value.LocalRoot;
        Assert.Single(Directory.GetFiles(root, "*", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NotebookPayloadConcurrentWritersAreSerialized(bool duplicate)
    {
        var (patients, _, _) = await NotebookPatients("Doctor", ["notebook.read", "notebook.write"]);
        using var web = Client(); var enrolled = await Enroll(web);
        using var mobile = Client(false); var token = await MobileComplete(mobile, enrolled.Key);
        var page = await CreateNotebookPageOk(web, patients[0].Id, new { title = "Race" });
        var id = page.GetProperty("id").GetGuid(); var version = page.GetProperty("rowVersion").GetString()!;
        var barrier = new NotebookRaceStorage(_factory.Services.GetRequiredService<IFileStorage>());
        using var host = _factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<IFileStorage>(barrier)));
        using var concurrent = host.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        concurrent.DefaultRequestHeaders.Authorization = new("Bearer", token);
        var responses = await Task.WhenAll(UploadNotebook(concurrent, patients[0].Id, id, version, "a"),
            UploadNotebook(concurrent, patients[0].Id, id, version, duplicate ? "a" : "b"));
        Assert.Single(responses, x => x.StatusCode == HttpStatusCode.Created);
        Assert.Single(responses, x => x.StatusCode == (duplicate ? HttpStatusCode.OK : HttpStatusCode.Conflict));
        foreach (var response in responses) response.Dispose();
        await using var db = _database.CreateContext();
        Assert.Equal(2, await db.Set<NotebookRevision>().CountAsync());
        Assert.Equal(1, await db.Set<StoredFile>().CountAsync());
        Assert.Equal(2, (await db.Set<NotebookPage>().SingleAsync()).CurrentRevisionNumber);
        Assert.Single(await db.AuditEvents.Where(x => x.ActionCode == "notebook.revision.save").ToArrayAsync());
        var root = _factory.Services.GetRequiredService<IOptions<FileStorageOptions>>().Value.LocalRoot;
        Assert.Single(Directory.GetFiles(root, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task NotebookPayloadUploadBoundsAndCookieCsrfPrecedePersistence()
    {
        var (patients, _, _) = await NotebookPatients("Doctor", ["notebook.read", "notebook.write"]);
        using var web = await NotebookSession();
        var page = await CreateNotebookPageOk(web, patients[0].Id, new { title = "Limits" });
        var id = page.GetProperty("id").GetGuid(); var version = page.GetProperty("rowVersion").GetString()!;
        Token(web, "invalid");
        Assert.Equal(HttpStatusCode.BadRequest, (await UploadNotebook(web, patients[0].Id, id, version, "a")).StatusCode);
        await Csrf(web);
        using var oversized = await UploadNotebook(web, patients[0].Id, id, version, "a", new byte[17000]);
        Assert.True(oversized.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.RequestEntityTooLarge);
        Assert.Equal(HttpStatusCode.BadRequest, (await UploadNotebook(web, patients[0].Id, id, version, "a", [])).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await UploadNotebook(web, patients[0].Id, id, version, new string('x', 129))).StatusCode);
        await using var db = _database.CreateContext();
        Assert.Equal(1, await db.Set<NotebookRevision>().CountAsync());
        Assert.Empty(await db.Set<StoredFile>().ToArrayAsync());
    }

    [Theory]
    [InlineData("DoctorAssistant", HttpStatusCode.OK)]
    [InlineData("Receptionist", HttpStatusCode.Forbidden)]
    public async Task NotebookPayloadRolesRemainReadOnlyOrDenied(string role, HttpStatusCode readStatus)
    {
        var (patients, _, _) = await NotebookPatients("Doctor", ["notebook.read", "notebook.write"]);
        using var web = await NotebookSession();
        var page = await CreateNotebookPageOk(web, patients[0].Id, new { title = "Note" });
        var id = page.GetProperty("id").GetGuid(); var version = page.GetProperty("rowVersion").GetString()!;
        Assert.Equal(HttpStatusCode.Created, (await UploadNotebook(web, patients[0].Id, id, version, "a")).StatusCode);
        using (var scope = _factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<Clinic.Infrastructure.Authentication.StaffAdministration>()
                .ChangeAsync(_id, "set-grants", approvedRoles: [role], permissions: ["notebook.read", "notebook.write"], patientScopes: [patients[0].Id]);
        // Issue fresh real mobile credentials after the security-stamp/role change.
        using var setup = _factory.Services.CreateScope();
        var users = setup.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<Clinic.Infrastructure.Authentication.StaffUser>>();
        var key = await users.GetAuthenticatorKeyAsync((await users.FindByIdAsync(_id))!);
        using var mobile = Client(false); await MobileComplete(mobile, key!);
        Assert.Equal(HttpStatusCode.Forbidden, (await UploadNotebook(mobile, patients[0].Id, id, version, "b")).StatusCode);
        Assert.Equal(readStatus, (await mobile.GetAsync(PayloadRoute(patients[0].Id, id))).StatusCode);
    }

    [Fact]
    public async Task NotebookPayloadReadFailsClosedOnAuditOrIntegrityFailure()
    {
        var (patients, _, _) = await NotebookPatients("Doctor", ["notebook.read", "notebook.write"]);
        using var web = Client(); var enrolled = await Enroll(web);
        using var mobile = Client(false); var token = await MobileComplete(mobile, enrolled.Key);
        var page = await CreateNotebookPageOk(mobile, patients[0].Id, new { title = "Note" });
        var id = page.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.Created, (await UploadNotebook(mobile, patients[0].Id, id, page.GetProperty("rowVersion").GetString()!, "a")).StatusCode);
        using var failedHost = _factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddScoped<IAccessAuditWriter, ContextAuditFailure>()));
        using var failed = failedHost.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        failed.DefaultRequestHeaders.Authorization = new("Bearer", token);
        using var read = await failed.GetAsync(PayloadRoute(patients[0].Id, id));
        Assert.Equal(HttpStatusCode.InternalServerError, read.StatusCode);
        Assert.DoesNotContain("formatVersion", await Body(read));
        await using var db = _database.CreateContext();
        var file = await db.Set<StoredFile>().SingleAsync();
        var root = _factory.Services.GetRequiredService<IOptions<FileStorageOptions>>().Value.LocalRoot;
        await File.WriteAllTextAsync(Path.Combine(root, file.StorageKey), "corrupted");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await mobile.GetAsync(PayloadRoute(patients[0].Id, id))).StatusCode);
        Assert.False(await db.AuditEvents.AnyAsync(x => x.ActionCode == "notebook.revision.read"));
    }
}
