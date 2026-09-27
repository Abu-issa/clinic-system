using System.Net;
using System.Net.Http.Json;
using Clinic.Application.Notebook;
using Clinic.Application.Storage;
using Clinic.Domain.Entities;
using MessagePack;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Clinic.IntegrationTests.Api;

public sealed partial class StaffIdentityHttpTests
{
    private static byte[] RealNotebookInk(Guid patient, Guid page) => MessagePackSerializer.Serialize(new Dictionary<string, object?>
    {
        ["formatVersion"] = 2, ["patientId"] = patient.ToString(), ["pageId"] = page.ToString(),
        ["pageWidthUnits"] = 210d, ["pageHeightUnits"] = 297d,
        ["strokes"] = new object[] { new Dictionary<string, object?> {
            ["id"] = 7, ["color"] = 0xff1565c0L, ["width"] = .7,
            ["points"] = new object[] { new object?[] { 20d, 30d, null, 0L }, new object[] { 40d, 60d, .8, 10000L } }
        } }
    });

    [Fact]
    public async Task RealInkRoundTripRetryConflictAmendmentAndIntegrity()
    {
        var (patients, _, _) = await NotebookPatients("Doctor", ["notebook.read", "notebook.write"]);
        using var web = await NotebookSession();
        var page = await CreateNotebookPageOk(web, patients[0].Id, new { title = "Ink" });
        var id = page.GetProperty("id").GetGuid(); var version = page.GetProperty("rowVersion").GetString()!;
        var ink = RealNotebookInk(patients[0].Id, id);
        using var save = await UploadNotebook(web, patients[0].Id, id, version, "real-ink", ink);
        Assert.Equal(HttpStatusCode.Created, save.StatusCode);
        var saved = await Json(save);
        using var replay = await UploadNotebook(web, patients[0].Id, id, version, "real-ink", ink);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.True((await Json(replay)).GetProperty("replayed").GetBoolean());
        using var stale = await UploadNotebook(web, patients[0].Id, id, version, "new-ink", ink);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("page_changed", (await Json(stale)).GetProperty("code").GetString());
        using var read = await web.GetAsync(PayloadRoute(patients[0].Id, id));
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal("application/msgpack", read.Content.Headers.ContentType!.MediaType);
        Assert.True(read.Headers.CacheControl?.NoStore);
        Assert.Equal(ink, await read.Content.ReadAsByteArrayAsync());
        using var finalize = await web.PostAsJsonAsync($"/api/staff/patients/{patients[0].Id}/notebook/pages/{id}/finalize",
            new { expectedRowVersion = saved.GetProperty("rowVersion").GetString() });
        Assert.Equal(HttpStatusCode.OK, finalize.StatusCode);
        var finalized = await Json(finalize);
        using var amend = await UploadNotebook(web, patients[0].Id, id,
            finalized.GetProperty("rowVersion").GetString()!, "amended-ink", ink, amendment: true);
        Assert.Equal(HttpStatusCode.Created, amend.StatusCode);
        using var amendedRead = await web.GetAsync(PayloadRoute(patients[0].Id, id, 3));
        Assert.Equal(ink, await amendedRead.Content.ReadAsByteArrayAsync());
        await using var db = _database.CreateContext();
        var file = await db.Set<StoredFile>().FirstAsync();
        var root = _factory.Services.GetRequiredService<IOptions<FileStorageOptions>>().Value.LocalRoot;
        await File.WriteAllBytesAsync(Path.Combine(root, file.StorageKey), [0x80]);
        var affected = await db.Set<NotebookRevision>().SingleAsync(x => x.StoredFileId == file.Id);
        using var corrupt = await web.GetAsync(PayloadRoute(patients[0].Id, id, affected.RevisionNumber));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, corrupt.StatusCode);
    }

    [Fact]
    public async Task RealInkMalformedOwnershipAndOversizeFailWithoutPersistence()
    {
        var (patients, _, _) = await NotebookPatients("Doctor", ["notebook.read", "notebook.write"]);
        using var web = await NotebookSession();
        var page = await CreateNotebookPageOk(web, patients[0].Id, new { title = "Bounds" });
        var id = page.GetProperty("id").GetGuid(); var version = page.GetProperty("rowVersion").GetString()!;
        foreach (var bytes in new[] { new byte[] { 0x86 }, RealNotebookInk(patients[1].Id, id), RealNotebookInk(patients[0].Id, Guid.NewGuid()) })
        {
            using var invalid = await UploadNotebook(web, patients[0].Id, id, version, "bad", bytes);
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        }
        using var oversized = await UploadNotebook(web, patients[0].Id, id, version, "big", new byte[NotebookPayloadService.MaxBytes + 1]);
        Assert.True(oversized.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.RequestEntityTooLarge);
        await using var db = _database.CreateContext();
        Assert.Empty(await db.Set<StoredFile>().ToArrayAsync());
    }
}
