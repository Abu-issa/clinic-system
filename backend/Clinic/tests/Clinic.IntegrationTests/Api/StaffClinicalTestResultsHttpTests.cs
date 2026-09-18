using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Clinic.Application.Storage;
using Clinic.Domain.Entities;
using Clinic.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Clinic.IntegrationTests.Api;

public sealed partial class StaffClinicalTestsHttpTests
{
    private static readonly string[] ResultPermissions = ["tests.read", "tests.write", "attachments.read", "attachments.write"];
    private static readonly byte[] ResultPdf = "%PDF-1.7 synthetic result"u8.ToArray();
    private static byte[] jsonVersion(string text) => JsonDocument.Parse(text).RootElement.GetProperty("rowVersion").GetBytesFromBase64();
    private async Task<byte[]> Version(Guid id)
    {
        await using var db = database.CreateContext();
        return (await db.ClinicalTestRequests.SingleAsync(x => x.Id == id)).RowVersion;
    }
    private static MultipartFormDataContent ResultBody(byte[] version, byte[]? bytes = null, string filename = "result.pdf", string mime = "application/pdf", int files = 1)
    {
        var body = new MultipartFormDataContent();
        body.Add(new StringContent(Convert.ToBase64String(version)), "expectedRowVersion");
        for (var i = 0; i < files; i++)
        {
            var file = new ByteArrayContent(bytes ?? ResultPdf);
            file.Headers.ContentType = MediaTypeHeaderValue.Parse(mime);
            body.Add(file, "file", filename);
        }
        return body;
    }
    private static string ResultPath(Harness h, Guid id) => $"{h.PatientPath}/test-requests/{id}";
    private static Task<HttpResponseMessage> UploadResult(HttpClient client, Harness h, Guid id, byte[] version) =>
        client.PostAsync(ResultPath(h, id) + "/results", ResultBody(version));
    private static Task<HttpResponseMessage> ReviewResult(HttpClient client, Harness h, Guid id, byte[] version, bool complete = false) =>
        client.PostAsJsonAsync(ResultPath(h, id) + (complete ? "/review/complete" : "/review/start"), new { expectedRowVersion = version });
    private static void NoObjects(Harness h)
    {
        var root = h.Factory.Services.GetRequiredService<IOptions<FileStorageOptions>>().Value.LocalRoot;
        Assert.False(Directory.Exists(root) && Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Any());
    }
    private async Task NoResults(Harness h, Guid id)
    {
        await using var db = database.CreateContext();
        Assert.False(await db.Set<ClinicalTestResultAttachment>().AnyAsync(x => x.ClinicalTestRequestId == id));
        Assert.False(await db.PatientAttachments.AnyAsync(x => x.PatientId == h.Patient.Id));
        Assert.False(await db.AuditEvents.AnyAsync(x => x.PatientId == h.Patient.Id && (x.ActionCode == "file.upload" || x.ActionCode.StartsWith("test-request.result"))));
        Assert.Equal(ClinicalTestStatus.Requested, (await db.ClinicalTestRequests.SingleAsync(x => x.Id == id)).Status);
        NoObjects(h);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MultipleResultsFullLifecycleDownloadsAndAudits(bool visitLinked)
    {
        using var h = await Seed();
        var (client, actor) = await Client(h, permissions: ResultPermissions);
        using var owned = client;
        await WithCsrf(client);
        var id = await CreateOrder(client, h, visitLinked ? h.Visit.Id : null);
        var version = await Version(id);
        using var premature = await ReviewResult(client, h, id, version);
        Assert.Equal(HttpStatusCode.Conflict, premature.StatusCode);
        for (var i = 1; i <= 3; i++)
        {
            using var upload = await UploadResult(client, h, id, version);
            Assert.Equal(HttpStatusCode.OK, upload.StatusCode);
            var text = await upload.Content.ReadAsStringAsync();
            var json = JsonDocument.Parse(text).RootElement;
            Assert.Equal(i, json.GetProperty("resultAttachmentCount").GetInt32());
            Assert.Equal(i == 3 ? "UnderReview" : "Uploaded", json.GetProperty("status").GetString());
            Assert.Equal(ServerNow, json.GetProperty("uploadedAtUtc").GetDateTimeOffset());
            var next = jsonVersion(text);
            Assert.False(version.SequenceEqual(next));
            version = next;
            if (i == 2)
            {
                using var start = await ReviewResult(client, h, id, version);
                Assert.Equal(HttpStatusCode.OK, start.StatusCode);
                version = jsonVersion(await start.Content.ReadAsStringAsync());
            }
        }
        using var complete = await ReviewResult(client, h, id, version, true);
        Assert.Equal(HttpStatusCode.OK, complete.StatusCode);
        version = jsonVersion(await complete.Content.ReadAsStringAsync());
        using var late = await UploadResult(client, h, id, version);
        Assert.Equal(HttpStatusCode.Conflict, late.StatusCode);
        using var restart = await ReviewResult(client, h, id, version);
        Assert.Equal(HttpStatusCode.Conflict, restart.StatusCode);
        using var repeat = await ReviewResult(client, h, id, version, true);
        Assert.Equal(HttpStatusCode.Conflict, repeat.StatusCode);
        using var details = await client.GetAsync(ResultPath(h, id));
        var detailText = await details.Content.ReadAsStringAsync();
        var attachments = JsonDocument.Parse(detailText).RootElement.GetProperty("resultAttachments");
        Assert.Equal(3, attachments.GetArrayLength());
        foreach (var hidden in new[] { "storageKey", "sha256", "storedFileId", "createdByStaffId", "reviewedByDoctorId" }) Assert.DoesNotContain(hidden, detailText);
        foreach (var item in attachments.EnumerateArray())
        {
            using var download = await client.GetAsync($"{h.PatientPath}/attachments/{item.GetProperty("attachmentId").GetGuid()}/download");
            Assert.Equal(HttpStatusCode.OK, download.StatusCode);
            Assert.Equal(ResultPdf, await download.Content.ReadAsByteArrayAsync());
        }
        await using var fresh = database.CreateContext();
        var request = await fresh.ClinicalTestRequests.SingleAsync(x => x.Id == id);
        Assert.Equal(h.Doctor.Id, request.ReviewedByDoctorId);
        Assert.Equal(ServerNow, request.ReviewedAtUtc);
        var rows = await fresh.PatientAttachments.Include(x => x.File).Where(x => x.PatientId == h.Patient.Id).ToArrayAsync();
        Assert.Equal(3, rows.Length);
        Assert.All(rows, x => Assert.Equal(visitLinked ? h.Visit.Id : (Guid?)null, x.VisitId));
        Assert.Equal(3, rows.Select(x => x.StoredFileId).Distinct().Count());
        Assert.Equal(3, rows.Select(x => x.File.StorageKey).Distinct().Count());
        Assert.Single(rows.Select(x => x.File.Sha256).Distinct());
        var events = await fresh.AuditEvents.Where(x => x.PatientId == h.Patient.Id).ToArrayAsync();
        Assert.Equal(3, events.Count(x => x.ActionCode == "file.upload"));
        Assert.Equal(3, events.Count(x => x.ActionCode == "test-request.result.upload"));
        Assert.Single(events, x => x.ActionCode == "test-request.review.start");
        Assert.Single(events, x => x.ActionCode == "test-request.review.complete");
        Assert.All(events, x => { Assert.Empty(x.Metadata); Assert.Equal(actor, x.ActorStaffId); Assert.Equal(AuditOutcome.Succeeded, x.Outcome); });
    }

    [Theory]
    [InlineData("Doctor", true, true, true, true, true, true)]
    [InlineData("DoctorAssistant", true, true, true, true, false, true)]
    [InlineData("Receptionist", true, true, true, true, true, false)]
    [InlineData("Doctor", false, true, true, true, true, false)]
    [InlineData("Doctor", true, false, true, true, true, false)]
    [InlineData("Doctor", true, true, false, true, true, false)]
    [InlineData("Doctor", true, true, true, false, true, false)]
    [InlineData("Doctor", true, true, true, true, false, false)]
    [InlineData("DoctorAssistant", false, true, true, true, false, false)]
    [InlineData("DoctorAssistant", true, false, true, true, false, false)]
    [InlineData("DoctorAssistant", true, true, false, true, false, false)]
    [InlineData("DoctorAssistant", true, true, true, false, false, false)]
    public async Task ResultUploadAuthorizationMatrix(string role, bool mfa, bool tests, bool attachments, bool scope, bool association, bool allowed)
    {
        using var h = await Seed();
        var (writer, _) = await Client(h);
        using var ownedWriter = writer;
        await WithCsrf(writer);
        var id = await CreateOrder(writer, h, h.Visit.Id);
        var permissions = new List<string>();
        if (tests) permissions.Add("tests.write");
        if (attachments) permissions.Add("attachments.write");
        var (client, _) = await Client(h, role, scope ? h.Patient.Id : Guid.NewGuid(), permissions.ToArray(), mfa, association);
        using var owned = client;
        await WithCsrf(client);
        using var response = await UploadResult(client, h, id, await Version(id));
        Assert.Equal(allowed ? HttpStatusCode.OK : HttpStatusCode.Forbidden, response.StatusCode);
        if (!allowed) await NoResults(h, id);
    }

    [Theory]
    [InlineData("DoctorAssistant", false)]
    [InlineData("DoctorAssistant", true)]
    [InlineData("Receptionist", false)]
    [InlineData("Receptionist", true)]
    [InlineData("WrongDoctor", false)]
    [InlineData("WrongDoctor", true)]
    public async Task ReviewRequiresDoctorAndLiveVisitAuthority(string role, bool complete)
    {
        using var h = await Seed();
        var (writer, _) = await Client(h, permissions: ResultPermissions);
        using var ownedWriter = writer;
        await WithCsrf(writer);
        var id = await CreateOrder(writer, h, h.Visit.Id);
        using var upload = await UploadResult(writer, h, id, await Version(id));
        Assert.Equal(HttpStatusCode.OK, upload.StatusCode);
        if (complete) { using var start = await ReviewResult(writer, h, id, await Version(id)); Assert.Equal(HttpStatusCode.OK, start.StatusCode); }
        var (client, actor) = await Client(h, role == "WrongDoctor" ? "Doctor" : role, permissions: ResultPermissions);
        using var owned = client;
        await WithCsrf(client);
        if (role == "WrongDoctor")
        {
            await using var db = database.CreateContext();
            var other = new Doctor("Other"); db.Add(other);
            (await db.Users.SingleAsync(x => x.Id == actor)).AssociatedDoctorId = other.Id;
            await db.SaveChangesAsync();
        }
        using var response = await ReviewResult(client, h, id, await Version(id), complete);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await using var fresh = database.CreateContext();
        Assert.False(await fresh.AuditEvents.AnyAsync(x => x.ActorStaffId == actor && x.ActionCode.StartsWith("test-request.review")));
    }

    [Theory]
    [InlineData("result.pdf", "application/pdf", true)]
    [InlineData("result.jpg", "image/jpeg", true)]
    [InlineData("result.png", "image/png", true)]
    [InlineData("result.pdf", "application/pdf", false)]
    [InlineData("result.svg", "image/svg+xml", false)]
    [InlineData("result.png", "application/pdf", false)]
    public async Task ResultFilePolicy(string filename, string mime, bool valid)
    {
        using var h = await Seed();
        var (client, _) = await Client(h, permissions: ResultPermissions);
        using var owned = client; await WithCsrf(client);
        var id = await CreateOrder(client, h);
        byte[] bytes = valid ? mime switch { "image/jpeg" => [0xff, 0xd8, 0xff, 0xe0, 0, 0], "image/png" => [137, 80, 78, 71, 13, 10, 26, 10, 0], _ => ResultPdf } : "fake bytes"u8.ToArray();
        using var response = await client.PostAsync(ResultPath(h, id) + "/results", ResultBody(await Version(id), bytes, filename, mime));
        Assert.Equal(valid ? HttpStatusCode.OK : HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        if (!valid) await NoResults(h, id);
    }

    [Theory]
    [InlineData("upload")]
    [InlineData("start")]
    [InlineData("complete")]
    public async Task CsrfRejectsEveryMutation(string command)
    {
        using var h = await Seed();
        var (client, _) = await Client(h, permissions: ResultPermissions);
        using var owned = client; await WithCsrf(client);
        var id = await CreateOrder(client, h);
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        using var response = command == "upload" ? await UploadResult(client, h, id, await Version(id))
            : await ReviewResult(client, h, id, await Version(id), command == "complete");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await NoResults(h, id);
    }

    [Fact]
    public async Task StaleTokensRejectAllCommandsAndRefreshedRetrySucceeds()
    {
        using var h = await Seed();
        var (client, _) = await Client(h, permissions: ResultPermissions);
        using var owned = client; await WithCsrf(client);
        var id = await CreateOrder(client, h); var original = await Version(id);
        using var first = await UploadResult(client, h, id, original);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        using var staleUpload = await UploadResult(client, h, id, original);
        Assert.Equal(HttpStatusCode.Conflict, staleUpload.StatusCode);
        using var staleStart = await ReviewResult(client, h, id, original);
        Assert.Equal(HttpStatusCode.Conflict, staleStart.StatusCode);
        var uploadedVersion = await Version(id);
        using var start = await ReviewResult(client, h, id, uploadedVersion);
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        using var staleComplete = await ReviewResult(client, h, id, uploadedVersion, true);
        Assert.Equal(HttpStatusCode.Conflict, staleComplete.StatusCode);
        using var complete = await ReviewResult(client, h, id, await Version(id), true);
        Assert.Equal(HttpStatusCode.OK, complete.StatusCode);
        await using var db = database.CreateContext();
        Assert.Single(await db.Set<ClinicalTestResultAttachment>().Where(x => x.ClinicalTestRequestId == id).ToArrayAsync());
    }

    [Fact]
    public async Task DetailMetadataRequiresAttachmentReadAndRemainsFailClosed()
    {
        using var h = await Seed();
        var (writer, _) = await Client(h, permissions: ResultPermissions);
        using var owned = writer; await WithCsrf(writer);
        var id = await CreateOrder(writer, h);
        using var upload = await UploadResult(writer, h, id, await Version(id));
        Assert.Equal(HttpStatusCode.OK, upload.StatusCode);
        var (reader, _) = await Client(h, role: "DoctorAssistant", permissions: ["tests.read"]);
        using var ownedReader = reader;
        using var detail = await reader.GetAsync(ResultPath(h, id));
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        var text = await detail.Content.ReadAsStringAsync();
        Assert.DoesNotContain("result.pdf", text);
        Assert.Equal(JsonValueKind.Null, JsonDocument.Parse(text).RootElement.GetProperty("resultAttachments").ValueKind);
        using var full = await writer.GetAsync(ResultPath(h, id));
        Assert.Contains("result.pdf", await full.Content.ReadAsStringAsync());
        h.Failure.Enabled = true;
        using var failed = await writer.GetAsync(ResultPath(h, id));
        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        Assert.DoesNotContain("result.pdf", await failed.Content.ReadAsStringAsync());
        Assert.DoesNotContain("Sensitive", await failed.Content.ReadAsStringAsync());
        Assert.DoesNotContain(id.ToString(), await failed.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task ExactlyOneFileRequired(int files)
    {
        using var h = await Seed(); var (client, _) = await Client(h, permissions: ResultPermissions);
        using var owned = client; await WithCsrf(client); var id = await CreateOrder(client, h);
        using var response = await client.PostAsync(ResultPath(h, id) + "/results", ResultBody(await Version(id), files: files));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); await NoResults(h, id);
    }

    [Theory]
    [InlineData("tests.write", "upload")]
    [InlineData("attachments.write", "upload")]
    [InlineData("tests.read", "read")]
    [InlineData("attachments.read", "read")]
    [InlineData(null, "upload")]
    [InlineData("tests.write", "start")]
    [InlineData("tests.write", "complete")]
    public async Task ExistingSessionLosesResultAccessAfterGrantRemoval(string? permission, string command)
    {
        using var h = await Seed(); var (client, actor) = await Client(h, permissions: ResultPermissions);
        using var owned = client; await WithCsrf(client); var id = await CreateOrder(client, h);
        var version = await Version(id);
        await using (var db = database.CreateContext())
        {
            var claim = await db.UserClaims.SingleAsync(x => x.UserId == actor && x.ClaimType == (permission == null ? "patient_record_id" : "permission") && (permission == null || x.ClaimValue == permission));
            db.Remove(claim); await db.SaveChangesAsync();
        }
        using var response = command == "read" ? await client.GetAsync(ResultPath(h, id)) : command == "upload"
            ? await UploadResult(client, h, id, version) : await ReviewResult(client, h, id, version, command == "complete");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode); await NoResults(h, id);
    }

    [Fact]
    public async Task CrossPatientRequestAndDownloadStayHidden()
    {
        using var a = await Seed(); using var b = await Seed();
        var (ca, _) = await Client(a, permissions: ResultPermissions); using var oa = ca; await WithCsrf(ca);
        var (cb, _) = await Client(b, permissions: ResultPermissions); using var ob = cb; await WithCsrf(cb);
        var id = await CreateOrder(cb, b);
        using var denied = await UploadResult(ca, a, id, await Version(id));
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        using var upload = await UploadResult(cb, b, id, await Version(id)); Assert.Equal(HttpStatusCode.OK, upload.StatusCode);
        using var read = await ca.GetAsync(ResultPath(a, id)); Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
        using var review = await ReviewResult(ca, a, id, await Version(id)); Assert.Equal(HttpStatusCode.NotFound, review.StatusCode);
        await using var db = database.CreateContext();
        var attachment = await db.PatientAttachments.SingleAsync(x => x.PatientId == b.Patient.Id);
        using var download = await ca.GetAsync($"{a.PatientPath}/attachments/{attachment.Id}/download");
        Assert.Equal(HttpStatusCode.NotFound, download.StatusCode);
        Assert.False(await db.AuditEvents.AnyAsync(x => x.PatientId == a.Patient.Id));
    }
}
