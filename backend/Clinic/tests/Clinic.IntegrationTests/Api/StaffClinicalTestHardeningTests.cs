using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Clinic.Application.ClinicalTests;
using Clinic.Application.Storage;
using Clinic.Domain.Entities;
using Clinic.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Clinic.IntegrationTests.Api;

public sealed partial class StaffClinicalTestsHttpTests
{
    private sealed class AdvancingResultClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = ServerNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private async Task<ClinicalTestRequest> FreshRequest(Guid id)
    {
        await using var db = database.CreateContext();
        return await db.ClinicalTestRequests.AsNoTracking().SingleAsync(x => x.Id == id);
    }

    private async Task AssertLoserHasNoRowsOrObject(Harness h, Guid id, ResultSaveProbe probe, int count)
    {
        await using var db = database.CreateContext();
        var links = await db.Set<ClinicalTestResultAttachment>().Where(x => x.ClinicalTestRequestId == id).ToArrayAsync();
        Assert.Equal(count, links.Length);
        Assert.Equal(count, await db.PatientAttachments.CountAsync(x => x.PatientId == h.Patient.Id));
        Assert.Equal(count, await db.AuditEvents.CountAsync(x => x.PatientId == h.Patient.Id && x.ActionCode == "file.upload"));
        Assert.Equal(count, await db.AuditEvents.CountAsync(x => x.PatientId == h.Patient.Id && x.ActionCode == "test-request.result.upload"));
        using var scope = h.Factory.Services.CreateScope();
        var storage = scope.ServiceProvider.GetRequiredService<IFileStorage>();
        foreach (var obj in probe.Objects)
        {
            var won = links.Any(x => x.PatientAttachmentId == obj.Attachment);
            Assert.Equal(won, await db.Set<StoredFile>().AnyAsync(x => x.Id == obj.File));
            Assert.Equal(won, await db.PatientAttachments.AnyAsync(x => x.Id == obj.Attachment));
            Assert.Equal(won, await db.AuditEvents.AnyAsync(x => x.ActionCode == "file.upload" && x.ResourceId == obj.Attachment.ToString("N")));
            Assert.Equal(won, await storage.ExistsAsync(obj.Key));
        }
    }

    [Theory]
    [InlineData(ClinicalTestStatus.Requested)]
    [InlineData(ClinicalTestStatus.Uploaded)]
    [InlineData(ClinicalTestStatus.UnderReview)]
    public async Task HardeningUploadRacesPreserveStateFirstTimeAndAtomicWinner(ClinicalTestStatus state)
    {
        using var seed = await Seed(); var probe = new ResultSaveProbe(); var clock = new AdvancingResultClock();
        using var h = Instrument(seed, probe, clock: clock);
        var (client, _) = await Client(h, permissions: ResultPermissions); using var owned = client; await WithCsrf(client);
        var id = await CreateOrder(client, h);
        if (state != ClinicalTestStatus.Requested)
        {
            using var first = await UploadResult(client, h, id, await Version(id)); Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        }
        if (state == ClinicalTestStatus.UnderReview)
        {
            using var start = await ReviewResult(client, h, id, await Version(id)); Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        }
        var before = await FreshRequest(id); probe.Objects.Clear(); probe.Race = true; clock.Now = ServerNow.AddMinutes(1);
        var responses = await Task.WhenAll(UploadResult(client, h, id, before.RowVersion), UploadResult(client, h, id, before.RowVersion));
        try
        {
            Assert.Single(responses, x => x.StatusCode == HttpStatusCode.OK);
            Assert.True(responses.Count(x => x.StatusCode == HttpStatusCode.Conflict) == 1, string.Join("\n", probe.Failures));
        }
        finally { foreach (var response in responses) response.Dispose(); }
        Assert.Equal(2, probe.Objects.Count); // Both promoted before SQL decides.
        var count = state == ClinicalTestStatus.Requested ? 1 : 2;
        await AssertLoserHasNoRowsOrObject(h, id, probe, count);
        var after = await FreshRequest(id);
        Assert.Equal(state == ClinicalTestStatus.Requested ? ClinicalTestStatus.Uploaded : state, after.Status);
        Assert.Equal(before.UploadedAtUtc ?? clock.Now, after.UploadedAtUtc);
        Assert.False(before.RowVersion.SequenceEqual(after.RowVersion));
        probe.Race = false; clock.Now = ServerNow.AddMinutes(2);
        using var retry = await UploadResult(client, h, id, after.RowVersion); Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        var retried = await FreshRequest(id); Assert.Equal(after.UploadedAtUtc, retried.UploadedAtUtc); Assert.Equal(after.Status, retried.Status);
        await AssertLoserHasNoRowsOrObject(h, id, probe, count + 1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HardeningConcurrentReviewsHaveOneWinnerAndNoReviewerOverwrite(bool complete)
    {
        using var seed = await Seed(); var probe = new ResultSaveProbe(); using var h = Instrument(seed, probe);
        var (first, actorA) = await Client(h, permissions: ResultPermissions); using var ownedA = first; await WithCsrf(first);
        var id = await CreateOrder(first, h);
        using var upload = await UploadResult(first, h, id, await Version(id)); Assert.Equal(HttpStatusCode.OK, upload.StatusCode);
        if (complete) { using var start = await ReviewResult(first, h, id, await Version(id)); Assert.Equal(HttpStatusCode.OK, start.StatusCode); }
        var (second, actorB) = await Client(h, permissions: ResultPermissions); using var ownedB = second; await WithCsrf(second);
        var other = new Doctor("Other reviewer");
        await using (var db = database.CreateContext())
        {
            db.Add(other); (await db.Users.SingleAsync(x => x.Id == actorB)).AssociatedDoctorId = other.Id; await db.SaveChangesAsync();
        }
        var before = await FreshRequest(id); var arrived = 0; var both = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        probe.BeforeSave = async (db, ct) =>
        {
            if (!db.ChangeTracker.Entries<ClinicalTestRequest>().Any(x => x.State == EntityState.Modified)) return;
            if (Interlocked.Increment(ref arrived) == 2) both.TrySetResult();
            await both.Task.WaitAsync(TimeSpan.FromSeconds(20), ct);
        };
        var responses = await Task.WhenAll(ReviewResult(first, h, id, before.RowVersion, complete), ReviewResult(second, h, id, before.RowVersion, complete));
        var aWon = responses[0].StatusCode == HttpStatusCode.OK;
        try
        {
            Assert.Single(responses, x => x.StatusCode == HttpStatusCode.OK);
            Assert.True(responses.Count(x => x.StatusCode == HttpStatusCode.Conflict) == 1, string.Join("\n", probe.Failures));
        }
        finally { foreach (var response in responses) response.Dispose(); }
        Assert.Equal(2, arrived); var after = await FreshRequest(id);
        Assert.Equal(complete ? ClinicalTestStatus.Reviewed : ClinicalTestStatus.UnderReview, after.Status);
        Assert.Equal(before.UploadedAtUtc, after.UploadedAtUtc);
        Assert.Equal(complete ? (aWon ? h.Doctor.Id : other.Id) : (Guid?)null, after.ReviewedByDoctorId);
        Assert.Equal(complete ? ServerNow : (DateTimeOffset?)null, after.ReviewedAtUtc);
        await using var fresh = database.CreateContext();
        var action = complete ? "test-request.review.complete" : "test-request.review.start";
        var audit = Assert.Single(await fresh.AuditEvents.Where(x => x.PatientId == h.Patient.Id && x.ActionCode == action).ToArrayAsync());
        Assert.Equal(aWon ? actorA : actorB, audit.ActorStaffId);
        probe.BeforeSave = null;
        using var retry = await ReviewResult(aWon ? second : first, h, id, after.RowVersion, complete);
        Assert.Equal(HttpStatusCode.Conflict, retry.StatusCode);
        var final = await FreshRequest(id); Assert.Equal(after.ReviewedAtUtc, final.ReviewedAtUtc); Assert.Equal(after.ReviewedByDoctorId, final.ReviewedByDoctorId);
    }

    [Fact]
    public async Task HardeningReviewCommitsBeforePromotedUploadAndStaleUploadIsCompensated()
    {
        using var seed = await Seed(); var probe = new ResultSaveProbe(); using var h = Instrument(seed, probe);
        var (client, _) = await Client(h, permissions: ResultPermissions); using var owned = client; await WithCsrf(client);
        var id = await CreateOrder(client, h);
        using var first = await UploadResult(client, h, id, await Version(id)); Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        using var start = await ReviewResult(client, h, id, await Version(id)); Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        var before = await FreshRequest(id); probe.Objects.Clear();
        var promoted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        probe.BeforeSave = async (db, ct) =>
        {
            if (!db.ChangeTracker.Entries<StoredFile>().Any(x => x.State == EntityState.Added)) return;
            promoted.TrySetResult(); await release.Task.WaitAsync(TimeSpan.FromSeconds(20), ct);
        };
        var uploading = UploadResult(client, h, id, before.RowVersion);
        try
        {
            await promoted.Task.WaitAsync(TimeSpan.FromSeconds(20));
            using var complete = await ReviewResult(client, h, id, before.RowVersion, true);
            Assert.Equal(HttpStatusCode.OK, complete.StatusCode);
            Assert.Equal(ClinicalTestStatus.Reviewed, (await FreshRequest(id)).Status);
        }
        finally { release.TrySetResult(); }
        using var loser = await uploading; Assert.Equal(HttpStatusCode.Conflict, loser.StatusCode);
        Assert.Single(probe.Objects); await AssertLoserHasNoRowsOrObject(h, id, probe, 1);
        var after = await FreshRequest(id); Assert.Equal(ClinicalTestStatus.Reviewed, after.Status);
        Assert.Equal(before.UploadedAtUtc, after.UploadedAtUtc); Assert.Equal(h.Doctor.Id, after.ReviewedByDoctorId);
        await using var fresh = database.CreateContext();
        Assert.Single(await fresh.AuditEvents.Where(x => x.PatientId == h.Patient.Id && x.ActionCode == "test-request.review.complete").ToArrayAsync());
    }

    private static object ImmutableOrder(ClinicalTestRequest x) => new
    { x.PatientId, x.VisitId, x.RequestedByDoctorId, x.RequestedAtUtc, x.Category, x.TestName, x.ClinicalInstructions };

    [Fact]
    public async Task HardeningImmutableFieldsTimesAndResponseTokenRoundTrip()
    {
        using var seed = await Seed(); var clock = new AdvancingResultClock(); using var h = Instrument(seed, new(), clock: clock);
        var (client, _) = await Client(h, permissions: ResultPermissions); using var owned = client; await WithCsrf(client);
        var id = await CreateOrder(client, h, h.Visit.Id); var original = await FreshRequest(id); var token = original.RowVersion;
        async Task Accept(HttpResponseMessage response)
        {
            using (response)
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
                Assert.Equal(JsonValueKind.String, json.GetProperty("rowVersion").ValueKind);
                var next = json.GetProperty("rowVersion").GetBytesFromBase64(); Assert.Equal(8, next.Length); Assert.False(token.SequenceEqual(next));
                var persisted = await FreshRequest(id); Assert.Equal(persisted.RowVersion, next); token = next;
                Assert.Equal(ImmutableOrder(original), ImmutableOrder(persisted));
                Assert.Equal(ServerNow.AddMinutes(1), persisted.UploadedAtUtc);
            }
        }
        async Task Reject(bool complete)
        {
            var before = await FreshRequest(id);
            using var response = await ReviewResult(client, h, id, token, complete); Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Equal(before.RowVersion, (await FreshRequest(id)).RowVersion);
        }
        await Reject(false); clock.Now = ServerNow.AddMinutes(1); await Accept(await UploadResult(client, h, id, token));
        await Reject(true); clock.Now = ServerNow.AddMinutes(2); await Accept(await UploadResult(client, h, id, token));
        await Accept(await ReviewResult(client, h, id, token)); await Reject(false);
        clock.Now = ServerNow.AddMinutes(3); await Accept(await UploadResult(client, h, id, token));
        clock.Now = ServerNow.AddMinutes(4); await Accept(await ReviewResult(client, h, id, token, true));
        var reviewed = await FreshRequest(id); await Reject(false); await Reject(true);
        clock.Now = ServerNow.AddMinutes(5);
        using var late = await UploadResult(client, h, id, token); Assert.Equal(HttpStatusCode.Conflict, late.StatusCode);
        foreach (var method in new[] { HttpMethod.Put, HttpMethod.Patch, HttpMethod.Delete })
        {
            using var request = new HttpRequestMessage(method, ResultPath(h, id)) { Content = JsonContent.Create(new { reviewedByDoctorId = Guid.NewGuid(), status = "Requested" }) };
            using var response = await client.SendAsync(request); Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        }
        var final = await FreshRequest(id); Assert.Equal(ImmutableOrder(original), ImmutableOrder(final));
        Assert.Equal(reviewed.RowVersion, final.RowVersion); Assert.Equal(reviewed.ReviewedAtUtc, final.ReviewedAtUtc); Assert.Equal(reviewed.ReviewedByDoctorId, final.ReviewedByDoctorId);
        await using var fresh = database.CreateContext();
        Assert.Equal(9, await fresh.AuditEvents.CountAsync(x => x.PatientId == h.Patient.Id)); // create + 3 pairs + start + complete
    }

    [Theory]
    [InlineData("Doctor", true, true, true)]
    [InlineData("Doctor", true, false, true)]
    [InlineData("Doctor", false, true, true)]
    [InlineData("Doctor", true, true, false)]
    [InlineData("DoctorAssistant", true, true, true)]
    [InlineData("DoctorAssistant", true, false, true)]
    [InlineData("DoctorAssistant", false, true, true)]
    [InlineData("DoctorAssistant", true, true, false)]
    public async Task HardeningReadMetadataCountAndIndependentDownloadContract(string role, bool testsRead, bool attachmentsRead, bool scope)
    {
        using var h = await Seed(); var (writer, _) = await Client(h, permissions: ResultPermissions); using var ownedWriter = writer; await WithCsrf(writer);
        var id = await CreateOrder(writer, h); using var upload = await UploadResult(writer, h, id, await Version(id)); Assert.Equal(HttpStatusCode.OK, upload.StatusCode);
        Guid attachmentId;
        await using (var db = database.CreateContext()) attachmentId = (await db.PatientAttachments.SingleAsync(x => x.PatientId == h.Patient.Id)).Id;
        var permissions = new List<string>(); if (testsRead) permissions.Add("tests.read"); if (attachmentsRead) permissions.Add("attachments.read");
        var (reader, _) = await Client(h, role, scope ? h.Patient.Id : Guid.NewGuid(), permissions.ToArray(), associateDoctor: false); using var ownedReader = reader;
        using var detail = await reader.GetAsync(ResultPath(h, id));
        Assert.Equal(testsRead && scope ? HttpStatusCode.OK : HttpStatusCode.Forbidden, detail.StatusCode);
        if (detail.IsSuccessStatusCode)
        {
            var text = await detail.Content.ReadAsStringAsync(); var json = JsonDocument.Parse(text).RootElement;
            Assert.Equal(1, json.GetProperty("resultAttachmentCount").GetInt32()); // Deliberately clinical-test metadata.
            if (attachmentsRead) Assert.Equal(attachmentId, json.GetProperty("resultAttachments")[0].GetProperty("attachmentId").GetGuid());
            else
            {
                Assert.Equal(JsonValueKind.Null, json.GetProperty("resultAttachments").ValueKind);
                foreach (var secret in new[] { "result.pdf", "application/pdf", "sizeBytes", attachmentId.ToString() }) Assert.DoesNotContain(secret, text);
            }
            using var list = await reader.GetAsync(h.PatientPath + "/test-requests");
            var item = JsonDocument.Parse(await list.Content.ReadAsStringAsync()).RootElement.GetProperty("items")[0];
            Assert.Equal(1, item.GetProperty("resultAttachmentCount").GetInt32()); Assert.False(item.TryGetProperty("resultAttachments", out _));
        }
        using var download = await reader.GetAsync($"{h.PatientPath}/attachments/{attachmentId}/download");
        Assert.Equal(attachmentsRead && scope ? HttpStatusCode.OK : HttpStatusCode.Forbidden, download.StatusCode);
        if (download.IsSuccessStatusCode) Assert.Equal(ResultPdf, await download.Content.ReadAsByteArrayAsync());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task HardeningStandaloneReviewUsesLiveAssociationAndIgnoresClientIdentity(bool complete, bool hasAssociation)
    {
        using var h = await Seed(); var (client, actor) = await Client(h, permissions: ResultPermissions); using var owned = client; await WithCsrf(client);
        var id = await CreateOrder(client, h); using var upload = await UploadResult(client, h, id, await Version(id)); Assert.Equal(HttpStatusCode.OK, upload.StatusCode);
        if (complete) { using var start = await ReviewResult(client, h, id, await Version(id)); Assert.Equal(HttpStatusCode.OK, start.StatusCode); }
        var other = new Doctor("Live standalone reviewer");
        await using (var db = database.CreateContext())
        {
            db.Add(other); (await db.Users.SingleAsync(x => x.Id == actor)).AssociatedDoctorId = hasAssociation ? other.Id : null; await db.SaveChangesAsync();
        }
        var before = await FreshRequest(id);
        using var response = await client.PostAsJsonAsync(ResultPath(h, id) + (complete ? "/review/complete" : "/review/start"),
            new { expectedRowVersion = before.RowVersion, reviewedByDoctorId = h.Doctor.Id, reviewedAtUtc = ServerNow.AddYears(-1), status = "Requested" });
        Assert.Equal(hasAssociation ? HttpStatusCode.OK : HttpStatusCode.Forbidden, response.StatusCode);
        var after = await FreshRequest(id);
        Assert.Equal(hasAssociation && complete ? other.Id : (Guid?)null, after.ReviewedByDoctorId);
        if (!hasAssociation) Assert.Equal(before.RowVersion, after.RowVersion);
        if (hasAssociation && complete) Assert.Equal(ServerNow, after.ReviewedAtUtc);
    }

    [Fact]
    public async Task HardeningBothPatientScopesCannotAuthorizeCrossPatientLink()
    {
        using var a = await Seed(); using var b = await Seed();
        var (client, actor) = await Client(a, permissions: ResultPermissions,
            extraClaims: [new Claim("patient_record_id", b.Patient.Id.ToString())]); using var owned = client; await WithCsrf(client);
        var id = await CreateOrder(client, a);
        // Both resources exist and actor has both scopes; integrity still forbids the link.
        Guid attachmentId;
        await using (var db = database.CreateContext())
        {
            var file = new StoredFile($"clinic-files/{Guid.NewGuid():N}", "foreign.pdf", "application/pdf", 10, new string('a', 64), actor, ServerNow);
            var attachment = new PatientAttachment(b.Patient.Id, file.Id, null, actor, ServerNow); attachmentId = attachment.Id;
            db.AddRange(file, attachment); await db.SaveChangesAsync();
        }
        using var scope = a.Factory.Services.CreateScope(); var store = scope.ServiceProvider.GetRequiredService<IClinicalTestStore>();
        var requestA = (await store.LoadAsync(a.Patient.Id, id, default))!;
        await using (var db = database.CreateContext())
        {
            var attachmentB = await db.PatientAttachments.SingleAsync(x => x.Id == attachmentId);
            Assert.Throws<ArgumentException>(() => store.AddResult(new ClinicalTestResultAttachment(requestA, attachmentB, ServerNow)));
        }
        await store.SaveAsync(default);
        await using var fresh = database.CreateContext();
        Assert.False(await fresh.Set<ClinicalTestResultAttachment>().AnyAsync(x => x.ClinicalTestRequestId == id));
        Assert.Equal(ClinicalTestStatus.Requested, (await fresh.ClinicalTestRequests.SingleAsync(x => x.Id == id)).Status);
        Assert.Single(await fresh.AuditEvents.Where(x => x.PatientId == a.Patient.Id).ToArrayAsync());
    }

    private sealed class OwnedResultStream : Stream
    {
        private readonly MemoryStream inner = new(ResultPdf);
        public int Disposals { get; private set; }
        public int Reads { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) { Reads++; return inner.Read(buffer, offset, count); }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) { Reads++; return inner.ReadAsync(buffer, ct); }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
        protected override void Dispose(bool disposing)
        {
            if (disposing) { Disposals++; inner.Dispose(); }
            base.Dispose(disposing);
        }
        public override ValueTask DisposeAsync() { Disposals++; return inner.DisposeAsync(); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task HardeningAssistantCannotInjectReviewStateDuringUpload()
    {
        using var h = await Seed(); var (writer, _) = await Client(h, permissions: ResultPermissions); using var ownedWriter = writer; await WithCsrf(writer);
        var id = await CreateOrder(writer, h, h.Visit.Id);
        var (assistant, _) = await Client(h, "DoctorAssistant", permissions: ResultPermissions, associateDoctor: false); using var owned = assistant; await WithCsrf(assistant);
        using var body = ResultBody(await Version(id));
        body.Add(new StringContent("Reviewed"), "status"); body.Add(new StringContent(h.Doctor.Id.ToString()), "reviewedByDoctorId");
        body.Add(new StringContent(ServerNow.AddYears(-1).ToString("O")), "reviewedAtUtc");
        using var response = await assistant.PostAsync(ResultPath(h, id) + "/results", body); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var row = await FreshRequest(id); Assert.Equal(ClinicalTestStatus.Uploaded, row.Status); Assert.Null(row.ReviewedAtUtc); Assert.Null(row.ReviewedByDoctorId);
        foreach (var complete in new[] { false, true })
        {
            using var denied = await ReviewResult(assistant, h, id, row.RowVersion, complete); Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        }
        Assert.Equal(row.RowVersion, (await FreshRequest(id)).RowVersion);
    }

    [Theory]
    [InlineData("validation")]
    [InlineData("stale")]
    [InlineData("stage")]
    [InlineData("audit")]
    [InlineData("cancel")]
    [InlineData("success")]
    public async Task HardeningOpenedStreamHasExactlyOneOwnerOnEveryExit(string scenario)
    {
        using var seed = await Seed(); var failure = new FailResultSql("file.upload");
        var probe = new ResultSaveProbe { CancelSave = scenario == "cancel" };
        using var h = Instrument(seed, probe, failure, new StorageFaults { Failure = scenario == "stage" ? "stage" : null });
        var (client, actor) = await Client(h, permissions: ResultPermissions); using var owned = client; await WithCsrf(client);
        var id = await CreateOrder(client, h); var token = await Version(id); failure.Enabled = scenario == "audit";
        using var scope = h.Factory.Services.CreateScope(); var service = scope.ServiceProvider.GetRequiredService<ClinicalTestLifecycleService>();
        var stream = new OwnedResultStream(); ClinicalTestResult? result = null;
        var exception = await Record.ExceptionAsync(async () => result = await service.UploadAsync(h.Patient.Id, id,
            scenario == "validation" ? [] : scenario == "stale" ? new byte[8] : token, actor, true, stream, "result.pdf", "application/pdf"));
        Assert.Equal(1, stream.Disposals);
        if (scenario is "validation" or "stale")
        {
            Assert.Null(exception); Assert.Equal(0, stream.Reads);
            Assert.Equal(scenario == "validation" ? ClinicalTestError.InvalidInput : ClinicalTestError.Conflict, result!.Error);
        }
        else if (scenario == "success") { Assert.Null(exception); Assert.Equal(ClinicalTestError.None, result!.Error); Assert.True(stream.Reads > 0); }
        else Assert.NotNull(exception);
        if (scenario != "success") await NoResults(h, id);
    }
}
