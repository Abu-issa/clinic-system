using System.Data.Common;
using System.Net;
using Clinic.Application.Storage;
using Clinic.Domain.Entities;
using Clinic.Infrastructure.Persistence;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Clinic.IntegrationTests.Api;

public sealed partial class StaffIdentityHttpTests
{
    private sealed class NotebookCommitFailure : DbTransactionInterceptor
    {
        public bool Enabled { get; set; }
        public bool ThrowAfterCommit { get; set; }
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
            TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            if (Enabled && !ThrowAfterCommit && HasNotebookMutation(eventData.Context!))
                throw new InvalidOperationException("Synthetic commit failure");
            return ValueTask.FromResult(result);
        }
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            if (Enabled && ThrowAfterCommit && HasNotebookMutation(eventData.Context!))
                throw new InvalidOperationException("Synthetic lost commit acknowledgement");
            return Task.CompletedTask;
        }
        private static bool HasNotebookMutation(DbContext db) =>
            db.ChangeTracker.Entries<NotebookRevision>().Any(x => x.State == EntityState.Added) ||
            db.ChangeTracker.Entries<NotebookPage>().Any(x => x.State == EntityState.Modified);
    }

    [Fact]
    public async Task NotebookPayloadAmbiguousCommitKeepsCommittedBytesAndRetryResolvesIt()
    {
        var (patients, _, _) = await NotebookPatients("Doctor", ["notebook.read", "notebook.write"]);
        using var web = Client(); var enrolled = await Enroll(web);
        using var mobile = Client(false); var token = await MobileComplete(mobile, enrolled.Key);
        var page = await CreateNotebookPageOk(mobile, patients[0].Id, new { title = "Commit acknowledgement" });
        var id = page.GetProperty("id").GetGuid(); var version = page.GetProperty("rowVersion").GetString()!;
        var failure = new NotebookCommitFailure { Enabled = true, ThrowAfterCommit = true };
        using var host = _factory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
            s.AddDbContext<ClinicDbContext>(o => o.AddInterceptors(failure))));
        using var failing = host.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        failing.DefaultRequestHeaders.Authorization = new("Bearer", token);
        Assert.Equal(HttpStatusCode.InternalServerError, (await UploadNotebook(failing, patients[0].Id, id, version, "a")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await UploadNotebook(mobile, patients[0].Id, id, version, "a")).StatusCode);
        using var read = await mobile.GetAsync(PayloadRoute(patients[0].Id, id));
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal(NotebookBytes(patients[0].Id, id), await read.Content.ReadAsByteArrayAsync());
        await using var db = _database.CreateContext();
        Assert.Equal(2, await db.Set<NotebookRevision>().CountAsync());
        Assert.Equal(1, await db.Set<StoredFile>().CountAsync());
    }

    private sealed class NotebookStorageFailure(IFileStorage inner, string mode) : IFileStorage
    {
        public Task<StagedObject> StageAsync(Stream content, CancellationToken ct = default) =>
            mode == "stage" ? throw new IOException("Synthetic stage failure") : inner.StageAsync(content, ct);
        public Task PromoteAsync(string stagedKey, string storageKey, CancellationToken ct = default) =>
            mode == "promote" ? throw new IOException("Synthetic promotion failure") : inner.PromoteAsync(stagedKey, storageKey, ct);
        public Task<Stream?> OpenReadAsync(string key, CancellationToken ct = default) => inner.OpenReadAsync(key, ct);
        public Task<bool> ExistsAsync(string key, CancellationToken ct = default) => inner.ExistsAsync(key, ct);
        public Task DeleteAsync(string key, CancellationToken ct = default) => inner.DeleteAsync(key, ct);
    }

    private sealed class NotebookRaceStorage(IFileStorage inner) : IFileStorage
    {
        private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int stagedCount;
        public async Task<StagedObject> StageAsync(Stream content, CancellationToken ct = default)
        {
            var staged = await inner.StageAsync(content, ct);
            if (Interlocked.Increment(ref stagedCount) == 2) ready.TrySetResult();
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(20), ct);
            return staged;
        }
        public Task PromoteAsync(string stagedKey, string storageKey, CancellationToken ct = default) => inner.PromoteAsync(stagedKey, storageKey, ct);
        public Task<Stream?> OpenReadAsync(string key, CancellationToken ct = default) => inner.OpenReadAsync(key, ct);
        public Task<bool> ExistsAsync(string key, CancellationToken ct = default) => inner.ExistsAsync(key, ct);
        public Task DeleteAsync(string key, CancellationToken ct = default) => inner.DeleteAsync(key, ct);
    }

    [Theory]
    [InlineData("stage", false)]
    [InlineData("promote", false)]
    [InlineData("database", false)]
    [InlineData("commit", false)]
    [InlineData("stage", true)]
    [InlineData("promote", true)]
    [InlineData("database", true)]
    [InlineData("commit", true)]
    public async Task NotebookPayloadFailureLeavesNoRowsOrObjectsAndAllowsRetry(string mode, bool amendment)
    {
        var (patients, _, _) = await NotebookPatients("Doctor", ["notebook.read", "notebook.write"]);
        using var web = Client(); var enrolled = await Enroll(web);
        using var mobile = Client(false); var token = await MobileComplete(mobile, enrolled.Key);
        var page = await CreateNotebookPageOk(mobile, patients[0].Id, new { title = "Failure test" });
        var id = page.GetProperty("id").GetGuid(); var version = page.GetProperty("rowVersion").GetString()!;
        var storage = _factory.Services.GetRequiredService<IFileStorage>();
        if (amendment)
        {
            using var finalized = await FinalizeNotebook(mobile, patients[0].Id, id, version);
            Assert.Equal(HttpStatusCode.OK, finalized.StatusCode);
            version = (await Json(finalized)).GetProperty("rowVersion").GetString()!;
        }
        var auditFailure = new AccessAuditHttpTests.FailAuditInsert { Enabled = mode == "database" };
        var commitFailure = new NotebookCommitFailure { Enabled = mode == "commit" };
        using var host = _factory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        {
            s.AddSingleton<IFileStorage>(new NotebookStorageFailure(storage, mode));
            s.AddDbContext<ClinicDbContext>(o => o.AddInterceptors(auditFailure, commitFailure));
        }));
        using var failing = host.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        failing.DefaultRequestHeaders.Authorization = new("Bearer", token);
        using var response = await UploadNotebook(failing, patients[0].Id, id, version, "draft", amendment: amendment);
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.DoesNotContain("clinic-files", await Body(response));
        Assert.DoesNotContain("Synthetic", await Body(response));
        await using (var db = _database.CreateContext())
        {
            Assert.Equal(1, await db.Set<NotebookRevision>().CountAsync());
            Assert.Empty(await db.Set<StoredFile>().ToArrayAsync());
            var persisted = await db.Set<NotebookPage>().SingleAsync();
            Assert.Equal(version, Convert.ToBase64String(persisted.RowVersion));
            Assert.Equal(1, persisted.CurrentRevisionNumber);
            Assert.False(await db.AuditEvents.AnyAsync(x => x.ActionCode == "notebook.revision.save" || x.ActionCode == "notebook.revision.amend"));
            Assert.Equal(amendment, persisted.FinalizedAtUtc is not null);
        }
        var root = _factory.Services.GetRequiredService<IOptions<FileStorageOptions>>().Value.LocalRoot;
        Assert.Empty(Directory.Exists(root) ? Directory.GetFiles(root, "*", SearchOption.AllDirectories) : []);
        Assert.Equal(HttpStatusCode.Created, (await UploadNotebook(mobile, patients[0].Id, id, version, "draft", amendment: amendment)).StatusCode);
    }
}
