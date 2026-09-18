using System.Collections.Concurrent;
using System.Data.Common;
using System.Net;
using Clinic.Application.Attachments;
using Clinic.Application.ClinicalTests;
using Clinic.Application.Storage;
using Clinic.Domain.Entities;
using Clinic.Domain.Enums;
using Clinic.Infrastructure.Persistence;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace Clinic.IntegrationTests.Api;

public sealed partial class StaffClinicalTestsHttpTests
{
    private sealed class ResultExceptionProbe(ResultSaveProbe probe) : Microsoft.AspNetCore.Diagnostics.IExceptionHandler
    {
        public ValueTask<bool> TryHandleAsync(Microsoft.AspNetCore.Http.HttpContext context, Exception exception, CancellationToken ct)
        { probe.Failures.Add(exception.ToString()); return ValueTask.FromResult(false); }
    }
    private sealed class ResultSaveProbe : SaveChangesInterceptor
    {
        public ConcurrentBag<(Guid File, Guid Attachment, string Key)> Objects { get; } = [];
        public ConcurrentBag<string> Failures { get; } = [];
        public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
        {
            Failures.Add(eventData.Exception.ToString());
            return Task.CompletedTask;
        }
        public bool Race { get; set; }
        public bool CancelSave { get; set; }
        public Func<DbContext, CancellationToken, Task>? BeforeSave { get; set; }
        private readonly TaskCompletionSource both = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int arrivals;
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            var db = eventData.Context!;
            if (BeforeSave is not null) await BeforeSave(db, cancellationToken);
            var file = db.ChangeTracker.Entries<StoredFile>().SingleOrDefault(x => x.State == EntityState.Added);
            if (file is null) return result;
            var attachment = Assert.Single(db.ChangeTracker.Entries<PatientAttachment>(), x => x.State == EntityState.Added);
            Assert.Single(db.ChangeTracker.Entries<ClinicalTestResultAttachment>(), x => x.State == EntityState.Added);
            Assert.Single(db.ChangeTracker.Entries<ClinicalTestRequest>(), x => x.State == EntityState.Modified);
            var events = db.ChangeTracker.Entries<AuditEvent>().Where(x => x.State == EntityState.Added).ToArray();
            Assert.Equal(2, events.Length);
            Assert.Single(events, x => x.Entity.ActionCode == "file.upload");
            Assert.Single(events, x => x.Entity.ActionCode == "test-request.result.upload");
            Objects.Add((file.Entity.Id, attachment.Entity.Id, file.Entity.StorageKey));
            if (CancelSave) throw new OperationCanceledException(cancellationToken);
            if (Race)
            {
                if (Interlocked.Increment(ref arrivals) == 2) both.TrySetResult();
                await both.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
            }
            return result;
        }
    }

    private sealed class FailResultSql(string target) : DbCommandInterceptor
    {
        public bool Enabled { get; set; }
        public int Attempts { get; private set; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            var audit = target.Contains('.');
            if (Enabled && command.CommandText.Contains($"INSERT INTO [{(audit ? "AuditEvents" : target)}]", StringComparison.Ordinal)
                && (!audit || command.Parameters.Cast<DbParameter>().Any(p => Equals(p.Value, target))))
            {
                Attempts++;
                command.CommandText = "THROW 51000, 'Synthetic result SQL failure', 1;";
            }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class StorageFaults
    {
        public string? Failure { get; set; }
        public int Stages;
        public int Deletes;
    }
    private sealed class ResultStorage(IFileStorage inner, StorageFaults faults) : IFileStorage
    {
        public Task<StagedObject> StageAsync(Stream content, CancellationToken ct = default)
        {
            Interlocked.Increment(ref faults.Stages);
            if (faults.Failure == "stage") throw new IOException("Synthetic private path must remain hidden");
            if (faults.Failure == "cancel") throw new OperationCanceledException(ct);
            return inner.StageAsync(content, ct);
        }
        public Task PromoteAsync(string staged, string key, CancellationToken ct = default) =>
            faults.Failure == "promote" ? throw new IOException("Synthetic promotion failure") : inner.PromoteAsync(staged, key, ct);
        public Task DeleteAsync(string key, CancellationToken ct = default)
        {
            Interlocked.Increment(ref faults.Deletes);
            if (faults.Failure == "compensate" && !key.StartsWith(FileStorageKey.StagingPrefix, StringComparison.Ordinal))
                throw new IOException("Synthetic compensation failure");
            return inner.DeleteAsync(key, ct);
        }
        public Task<bool> ExistsAsync(string key, CancellationToken ct = default) => inner.ExistsAsync(key, ct);
        public Task<Stream?> OpenReadAsync(string key, CancellationToken ct = default) => inner.OpenReadAsync(key, ct);
    }

    private Harness Instrument(Harness seed, ResultSaveProbe probe, FailResultSql? failure = null,
        StorageFaults? storage = null, long? maxSize = null, StaffAttachmentHttpTests.BodyMeter? meter = null, TimeProvider? clock = null)
    {
        var factory = seed.Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            if (meter is not null) services.AddSingleton<Microsoft.AspNetCore.Hosting.IStartupFilter>(meter);
            if (clock is not null) services.AddSingleton(clock);
            services.Insert(0, ServiceDescriptor.Singleton<Microsoft.AspNetCore.Diagnostics.IExceptionHandler>(new ResultExceptionProbe(probe)));
            services.AddDbContext<ClinicDbContext>(o =>
            {
                o.UseSqlServer(database.ConnectionString, sql => sql.MaxBatchSize(1)).AddInterceptors(probe);
                if (failure is not null) o.AddInterceptors(failure);
            });
            if (maxSize is { } max) services.AddSingleton(new AttachmentOptions { MaxFileSizeBytes = max });
            if (storage is not null)
            {
                var descriptor = services.Last(x => x.ServiceType == typeof(IFileStorage));
                services.Remove(descriptor);
                services.AddScoped<IFileStorage>(sp => new ResultStorage((IFileStorage)(descriptor.ImplementationInstance
                    ?? descriptor.ImplementationFactory?.Invoke(sp)
                    ?? ActivatorUtilities.CreateInstance(sp, descriptor.ImplementationType!)), storage));
            }
        }));
        return new(factory, seed.Failure, seed.Patient, seed.Doctor, seed.Visit);
    }

    [Theory]
    [InlineData("file.upload", false)]
    [InlineData("test-request.result.upload", false)]
    [InlineData("StoredFiles", false)]
    [InlineData("PatientAttachments", false)]
    [InlineData("ClinicalTestResultAttachments", false)]
    [InlineData("test-request.result.upload", true)]
    public async Task ResultSqlFailuresRollbackEveryRowAndCompensate(string target, bool compensationFailure)
    {
        using var seed = await Seed(); var probe = new ResultSaveProbe(); var failure = new FailResultSql(target);
        var faults = new StorageFaults { Failure = compensationFailure ? "compensate" : null };
        using var h = Instrument(seed, probe, failure, faults);
        var (client, actor) = await Client(h, permissions: ResultPermissions); using var owned = client; await WithCsrf(client);
        var id = await CreateOrder(client, h); var version = await Version(id); failure.Enabled = true;
        using var response = await UploadResult(client, h, id, version);
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Synthetic", text); Assert.DoesNotContain("result.pdf", text);
        Assert.Contains(probe.Failures, x => x.Contains("Synthetic result SQL failure", StringComparison.Ordinal));
        Assert.DoesNotContain(probe.Failures, x => x.Contains("Synthetic compensation failure", StringComparison.Ordinal));
        Assert.Equal(1, failure.Attempts); var obj = Assert.Single(probe.Objects);
        await using var fresh = database.CreateContext();
        Assert.False(await fresh.Set<StoredFile>().AnyAsync(x => x.Id == obj.File || x.CreatedByStaffId == actor));
        Assert.False(await fresh.PatientAttachments.AnyAsync(x => x.PatientId == h.Patient.Id));
        Assert.False(await fresh.Set<ClinicalTestResultAttachment>().AnyAsync(x => x.ClinicalTestRequestId == id));
        Assert.Equal(version, (await fresh.ClinicalTestRequests.SingleAsync(x => x.Id == id)).RowVersion);
        var request = await fresh.ClinicalTestRequests.SingleAsync(x => x.Id == id);
        Assert.Equal(ClinicalTestStatus.Requested, request.Status); Assert.Null(request.UploadedAtUtc);
        Assert.Single(await fresh.AuditEvents.Where(x => x.PatientId == h.Patient.Id).ToArrayAsync());
        using var scope = h.Factory.Services.CreateScope();
        Assert.Equal(compensationFailure, await scope.ServiceProvider.GetRequiredService<IFileStorage>().ExistsAsync(obj.Key));
        Assert.True(faults.Deletes >= 2);
        if (!compensationFailure) NoObjects(h);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReviewAuditFailureRollsBackStatusReviewerTimeAndToken(bool complete)
    {
        using var seed = await Seed(); var failure = new FailResultSql(complete ? "test-request.review.complete" : "test-request.review.start");
        using var h = Instrument(seed, new(), failure);
        var (client, _) = await Client(h, permissions: ResultPermissions); using var owned = client; await WithCsrf(client);
        var id = await CreateOrder(client, h);
        using var upload = await UploadResult(client, h, id, await Version(id)); Assert.Equal(HttpStatusCode.OK, upload.StatusCode);
        if (complete) { using var start = await ReviewResult(client, h, id, await Version(id)); Assert.Equal(HttpStatusCode.OK, start.StatusCode); }
        var version = await Version(id); failure.Enabled = true;
        using var response = await ReviewResult(client, h, id, version, complete);
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode); Assert.Equal(1, failure.Attempts);
        await using var fresh = database.CreateContext(); var row = await fresh.ClinicalTestRequests.SingleAsync(x => x.Id == id);
        Assert.Equal(complete ? ClinicalTestStatus.UnderReview : ClinicalTestStatus.Uploaded, row.Status);
        Assert.Null(row.ReviewedAtUtc); Assert.Null(row.ReviewedByDoctorId); Assert.Equal(version, row.RowVersion);
        Assert.False(await fresh.AuditEvents.AnyAsync(x => x.PatientId == h.Patient.Id && x.ActionCode == (complete ? "test-request.review.complete" : "test-request.review.start")));
    }

    [Fact]
    public async Task ConcurrentFirstResultsHaveOneSqlWinnerAndLoserObjectIsDeleted()
    {
        using var seed = await Seed(); var probe = new ResultSaveProbe { Race = true }; using var h = Instrument(seed, probe);
        var (client, _) = await Client(h, permissions: ResultPermissions); using var owned = client; await WithCsrf(client);
        var id = await CreateOrder(client, h); var version = await Version(id);
        var results = await Task.WhenAll(UploadResult(client, h, id, version), UploadResult(client, h, id, version));
        try
        {
            Assert.Single(results, x => x.StatusCode == HttpStatusCode.OK);
            Assert.True(results.Count(x => x.StatusCode == HttpStatusCode.Conflict) == 1, string.Join("\n", probe.Failures));
        }
        finally { foreach (var response in results) response.Dispose(); }
        Assert.Equal(2, probe.Objects.Count);
        await using var fresh = database.CreateContext();
        var link = Assert.Single(await fresh.Set<ClinicalTestResultAttachment>().Where(x => x.ClinicalTestRequestId == id).ToArrayAsync());
        Assert.Single(await fresh.PatientAttachments.Where(x => x.PatientId == h.Patient.Id).ToArrayAsync());
        Assert.Equal(3, await fresh.AuditEvents.CountAsync(x => x.PatientId == h.Patient.Id));
        using var scope = h.Factory.Services.CreateScope(); var storage = scope.ServiceProvider.GetRequiredService<IFileStorage>();
        foreach (var obj in probe.Objects)
        {
            var won = obj.Attachment == link.PatientAttachmentId;
            Assert.Equal(won, await fresh.Set<StoredFile>().AnyAsync(x => x.Id == obj.File));
            Assert.Equal(won, await storage.ExistsAsync(obj.Key));
        }
        probe.Race = false;
        using var retry = await UploadResult(client, h, id, await Version(id)); Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
    }

    [Theory]
    [InlineData("stage")]
    [InlineData("promote")]
    public async Task StorageFailuresLeaveNoClinicalResult(string fault)
    {
        using var seed = await Seed(); var probe = new ResultSaveProbe(); var faults = new StorageFaults { Failure = fault };
        using var h = Instrument(seed, probe, storage: faults);
        var (client, _) = await Client(h, permissions: ResultPermissions); using var owned = client; await WithCsrf(client);
        var id = await CreateOrder(client, h);
        using var response = await UploadResult(client, h, id, await Version(id));
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.DoesNotContain("Synthetic", await response.Content.ReadAsStringAsync());
        Assert.Empty(probe.Objects); await NoResults(h, id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationPropagatesWithoutClinicalVisibility(bool afterPromotion)
    {
        using var seed = await Seed(); var faults = new StorageFaults { Failure = afterPromotion ? null : "cancel" };
        using var h = Instrument(seed, new() { CancelSave = afterPromotion }, storage: faults);
        var (client, actor) = await Client(h, permissions: ResultPermissions); using var owned = client; await WithCsrf(client);
        var id = await CreateOrder(client, h); var version = await Version(id);
        using var scope = h.Factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ClinicalTestLifecycleService>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.UploadAsync(h.Patient.Id, id, version, actor, true,
            new MemoryStream(ResultPdf), "result.pdf", "application/pdf"));
        await NoResults(h, id);
    }

    [Fact]
    public async Task FeatureSizeIsEnforcedWhileStreaming()
    {
        using var seed = await Seed(); using var h = Instrument(seed, new(), maxSize: 16);
        var (client, _) = await Client(h, permissions: ResultPermissions); using var owned = client; await WithCsrf(client);
        var id = await CreateOrder(client, h);
        using var response = await UploadResult(client, h, id, await Version(id));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode); await NoResults(h, id);
    }

    [Theory]
    [InlineData("csrf", HttpStatusCode.BadRequest)]
    [InlineData("invalid-csrf", HttpStatusCode.BadRequest)]
    [InlineData("wrong-doctor", HttpStatusCode.Forbidden)]
    [InlineData("foreign-request", HttpStatusCode.NotFound)]
    public async Task ResultGateRejectsBeforeReadingMultipart(string reason, HttpStatusCode expected)
    {
        using var seed = await Seed(); var meter = new StaffAttachmentHttpTests.BodyMeter(); var faults = new StorageFaults();
        using var h = Instrument(seed, new(), storage: faults, meter: meter);
        var (client, actor) = await Client(h, permissions: ResultPermissions); using var owned = client; await WithCsrf(client);
        var id = await CreateOrder(client, h, h.Visit.Id); var version = await Version(id);
        if (reason.Contains("csrf"))
        {
            client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
            if (reason == "invalid-csrf") client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", "invalid");
        }
        if (reason == "wrong-doctor")
        {
            await using var db = database.CreateContext(); var doctor = new Doctor("Other"); db.Add(doctor);
            (await db.Users.SingleAsync(x => x.Id == actor)).AssociatedDoctorId = doctor.Id; await db.SaveChangesAsync();
        }
        meter.Bytes = 0;
        using var response = await UploadResult(client, h, reason == "foreign-request" ? Guid.NewGuid() : id, version);
        Assert.Equal(expected, response.StatusCode); Assert.Equal(0, meter.Bytes); Assert.Equal(0, faults.Stages);
        await NoResults(h, id);
    }

    private sealed class ResultUnknownLengthMultipart : MultipartFormDataContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RequestCapReturns413AndValidLargeResultPassesSmallJsonControllerLimit(bool unknownLength)
    {
        using var seed = await Seed(); var probe = new ResultSaveProbe(); using var h = Instrument(seed, probe);
        var (client, _) = await Client(h, permissions: ResultPermissions); using var owned = client; await WithCsrf(client);
        var id = await CreateOrder(client, h);
        var bytes = ResultPdf.Concat(new byte[100_000]).ToArray();
        using var valid = await client.PostAsync(ResultPath(h, id) + "/results", ResultBody(await Version(id), bytes));
        Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
        var huge = ResultPdf.Concat(new byte[AttachmentOptions.DefaultMaxFileSizeBytes + 100_000]).ToArray();
        using var hugeBody = unknownLength ? new ResultUnknownLengthMultipart() : new MultipartFormDataContent();
        hugeBody.Add(new StringContent(Convert.ToBase64String(await Version(id))), "expectedRowVersion");
        var file = new ByteArrayContent(huge); file.Headers.ContentType = new("application/pdf"); hugeBody.Add(file, "file", "large.pdf");
        using var rejected = await client.PostAsync(ResultPath(h, id) + "/results", hugeBody);
        Assert.True(rejected.StatusCode == HttpStatusCode.RequestEntityTooLarge,
            rejected.StatusCode + " " + await rejected.Content.ReadAsStringAsync() + "\n" + string.Join("\n", probe.Failures));
        await using var db = database.CreateContext();
        Assert.Single(await db.Set<ClinicalTestResultAttachment>().Where(x => x.ClinicalTestRequestId == id).ToArrayAsync());
    }
}
