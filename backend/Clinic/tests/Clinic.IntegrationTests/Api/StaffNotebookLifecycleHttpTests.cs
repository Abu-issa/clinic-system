using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using Clinic.Domain.Entities;
using Clinic.Domain.Enums;
using Clinic.Infrastructure.Authentication;
using Clinic.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace Clinic.IntegrationTests.Api;

public sealed partial class StaffIdentityHttpTests
{
    private static Task<HttpResponseMessage> FinalizeNotebook(HttpClient client, Guid patient, Guid page, string version) =>
        client.PostAsJsonAsync($"/api/staff/patients/{patient}/notebook/pages/{page}/finalize", new { expectedRowVersion = version });

    [Fact]
    public async Task NotebookLifecycleFinalizeAndAmendPreserveHistoryAndAuditExactlyOnce()
    {
        var (patients, _, _) = await NotebookPatients("Doctor", ["notebook.read", "notebook.write"]);
        using var web = await NotebookSession();
        var page = await CreateNotebookPageOk(web, patients[0].Id, new { title = "Private notebook" });
        var patient = patients[0].Id; var id = page.GetProperty("id").GetGuid();
        var version = page.GetProperty("rowVersion").GetString()!;
        Assert.Equal(HttpStatusCode.Conflict, (await UploadNotebook(web, patient, id, version, "amend", amendment: true)).StatusCode);
        using var saved = await UploadNotebook(web, patient, id, version, "normal");
        Assert.Equal(HttpStatusCode.Created, saved.StatusCode);
        var uploadedVersion = (await Json(saved)).GetProperty("rowVersion").GetString()!;
        using var stale = await FinalizeNotebook(web, patient, id, version);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("page_changed", (await Json(stale)).GetProperty("code").GetString());
        _clock.Now = _clock.Now.AddSeconds(1);
        using var finalized = await web.PostAsJsonAsync($"/api/staff/patients/{patient}/notebook/pages/{id}/finalize",
            new { expectedRowVersion = uploadedVersion, finalizedByDoctorId = Guid.NewGuid(), finalizedAtUtc = "2000-01-01T00:00:00Z" });
        Assert.Equal(HttpStatusCode.OK, finalized.StatusCode);
        var final = await Json(finalized); var finalVersion = final.GetProperty("rowVersion").GetString()!;
        Assert.NotEqual(uploadedVersion, finalVersion);
        Assert.Equal(_doctor, final.GetProperty("finalizedByDoctorId").GetGuid());
        Assert.Equal(_clock.Now, final.GetProperty("finalizedAtUtc").GetDateTimeOffset());
        using var repeated = await FinalizeNotebook(web, patient, id, uploadedVersion);
        Assert.Equal(HttpStatusCode.OK, repeated.StatusCode);
        Assert.Equal(finalVersion, (await Json(repeated)).GetProperty("rowVersion").GetString());
        // Neither new normal payloads nor old normal retry keys bypass finalization.
        Assert.Equal(HttpStatusCode.Conflict, (await UploadNotebook(web, patient, id, finalVersion, "new")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await UploadNotebook(web, patient, id, uploadedVersion, "normal")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await UploadNotebook(web, patient, id, finalVersion, "normal", amendment: true)).StatusCode);
        _clock.Now = _clock.Now.AddSeconds(1);
        using var amended = await UploadNotebook(web, patient, id, finalVersion, "amend", amendment: true);
        Assert.Equal(HttpStatusCode.Created, amended.StatusCode);
        var amendment = await Json(amended);
        Assert.Equal(3, amendment.GetProperty("revisionNumber").GetInt64());
        using var retry = await UploadNotebook(web, patient, id, finalVersion, "amend", amendment: true);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal(amendment.GetProperty("revisionId").GetGuid(), (await Json(retry)).GetProperty("revisionId").GetGuid());
        Assert.Equal(HttpStatusCode.Conflict, (await UploadNotebook(web, patient, id, finalVersion, "amend", device: "other", amendment: true)).StatusCode);
        var changedBytes = System.Text.Encoding.UTF8.GetBytes(" " + System.Text.Encoding.UTF8.GetString(NotebookBytes(patient, id)));
        Assert.Equal(HttpStatusCode.Conflict, (await UploadNotebook(web, patient, id, finalVersion, "amend", changedBytes, amendment: true)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await UploadNotebook(web, patient, id, finalVersion, "next", amendment: true)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await web.GetAsync(PayloadRoute(patient, id, 2))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await web.GetAsync(PayloadRoute(patient, id, 3))).StatusCode);
        using var repeatedAfterAmend = await FinalizeNotebook(web, patient, id, uploadedVersion);
        var current = await Json(repeatedAfterAmend);
        Assert.Equal(final.GetProperty("finalizedAtUtc").GetDateTimeOffset(), current.GetProperty("finalizedAtUtc").GetDateTimeOffset());
        Assert.Equal(amendment.GetProperty("rowVersion").GetString(), current.GetProperty("rowVersion").GetString());
        await using var db = _database.CreateContext();
        Assert.Equal(3, await db.Set<NotebookRevision>().CountAsync());
        Assert.Equal(NotebookRevisionKind.Amendment, (await db.Set<NotebookRevision>().SingleAsync(x => x.RevisionNumber == 3)).Kind);
        var finalAudit = await db.AuditEvents.SingleAsync(x => x.ActionCode == "notebook.page.finalize");
        var amendAudit = await db.AuditEvents.SingleAsync(x => x.ActionCode == "notebook.revision.amend");
        Assert.Equal(_id, finalAudit.ActorStaffId); Assert.Equal(patient, finalAudit.PatientId);
        Assert.Equal(_id, amendAudit.ActorStaffId); Assert.Equal(patient, amendAudit.PatientId);
        Assert.Empty(finalAudit.Metadata); Assert.Empty(amendAudit.Metadata);
        var tracked = await db.Set<NotebookPage>().SingleAsync();
        db.Entry(tracked).Property(x => x.FinalizedAtUtc).CurrentValue = null;
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();
        tracked = await db.Set<NotebookPage>().SingleAsync();
        db.Entry(tracked).Property(x => x.FinalizedByDoctorId).CurrentValue = Guid.NewGuid();
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }

    [Theory]
    [InlineData("DoctorAssistant")]
    [InlineData("Receptionist")]
    public async Task NotebookLifecycleRolesCannotFinalizeOrAmend(string role)
    {
        var (patients, _, _) = await NotebookPatients("Doctor", ["notebook.read", "notebook.write"]);
        using var web = Client(); var enrolled = await Enroll(web);
        var page = await CreateNotebookPageOk(web, patients[0].Id, new { title = "Roles" });
        var id = page.GetProperty("id").GetGuid(); var version = page.GetProperty("rowVersion").GetString()!;
        using (var scope = _factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<StaffAdministration>().ChangeAsync(_id, "set-grants",
                approvedRoles: [role], permissions: ["notebook.read", "notebook.write"], patientScopes: [patients[0].Id]);
        using var mobile = Client(false); await MobileComplete(mobile, enrolled.Key);
        Assert.Equal(HttpStatusCode.Forbidden, (await FinalizeNotebook(mobile, patients[0].Id, id, version)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await UploadNotebook(mobile, patients[0].Id, id, version, "a", amendment: true)).StatusCode);
        await using var db = _database.CreateContext();
        Assert.Null((await db.Set<NotebookPage>().SingleAsync()).FinalizedAtUtc);
        Assert.False(await db.AuditEvents.AnyAsync(x => x.ActionCode == "notebook.page.finalize" || x.ActionCode == "notebook.revision.amend"));
    }

    [Fact]
    public async Task NotebookLifecycleRejectsRouteTamperingAndInvalidVersionAndCsrf()
    {
        var (patients, _, _) = await NotebookPatients("Doctor", ["notebook.read", "notebook.write"]);
        using var web = await NotebookSession();
        var page = await CreateNotebookPageOk(web, patients[0].Id, new { title = "Scope" });
        var id = page.GetProperty("id").GetGuid(); var version = page.GetProperty("rowVersion").GetString()!;
        Assert.Equal(HttpStatusCode.NotFound, (await FinalizeNotebook(web, patients[1].Id, id, version)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await FinalizeNotebook(web, patients[2].Id, id, version)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await FinalizeNotebook(web, patients[0].Id, id, "AA==")).StatusCode);
        Token(web, "invalid");
        Assert.Equal(HttpStatusCode.BadRequest, (await FinalizeNotebook(web, patients[0].Id, id, version)).StatusCode);
        await Csrf(web);
        Assert.Equal(HttpStatusCode.OK, (await FinalizeNotebook(web, patients[0].Id, id, version)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await UploadNotebook(web, patients[1].Id, id, version, "a", amendment: true)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await UploadNotebook(web, patients[2].Id, id, version, "a", amendment: true)).StatusCode);
    }

    [Theory]
    [InlineData("audit")]
    [InlineData("commit")]
    [InlineData("lost-acknowledgement")]
    public async Task NotebookLifecycleFinalizeFailureAndRetryAreAtomic(string mode)
    {
        var (patients, _, _) = await NotebookPatients("Doctor", ["notebook.read", "notebook.write"]);
        using var web = Client(); var enrolled = await Enroll(web);
        using var mobile = Client(false); var token = await MobileComplete(mobile, enrolled.Key);
        var page = await CreateNotebookPageOk(mobile, patients[0].Id, new { title = "Finalize failure" });
        var id = page.GetProperty("id").GetGuid(); var version = page.GetProperty("rowVersion").GetString()!;
        var audit = new AccessAuditHttpTests.FailAuditInsert { Enabled = mode == "audit" };
        var commit = new NotebookCommitFailure { Enabled = mode != "audit", ThrowAfterCommit = mode == "lost-acknowledgement" };
        using var host = _factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddDbContext<ClinicDbContext>(o => o.AddInterceptors(audit, commit))));
        using var failing = host.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        failing.DefaultRequestHeaders.Authorization = new("Bearer", token);
        Assert.Equal(HttpStatusCode.InternalServerError, (await FinalizeNotebook(failing, patients[0].Id, id, version)).StatusCode);
        await using (var db = _database.CreateContext())
        {
            var persisted = await db.Set<NotebookPage>().SingleAsync();
            Assert.Equal(mode == "lost-acknowledgement", persisted.FinalizedAtUtc is not null);
            Assert.Equal(mode == "lost-acknowledgement" ? 1 : 0, await db.AuditEvents.CountAsync(x => x.ActionCode == "notebook.page.finalize"));
            if (mode != "lost-acknowledgement") Assert.Equal(version, Convert.ToBase64String(persisted.RowVersion));
        }
        Assert.Equal(HttpStatusCode.OK, (await FinalizeNotebook(mobile, patients[0].Id, id, version)).StatusCode);
        await using var verify = _database.CreateContext();
        Assert.Equal(1, await verify.AuditEvents.CountAsync(x => x.ActionCode == "notebook.page.finalize"));
        Assert.Equal(1, await verify.Set<NotebookRevision>().CountAsync());
    }

    private sealed class NotebookCommandBarrier : DbCommandInterceptor
    {
        private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int arrivals;
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken ct = default)
        {
            if (command.CommandText.Contains("[NotebookPages] WITH (UPDLOCK, ROWLOCK)"))
            {
                if (Interlocked.Increment(ref arrivals) == 2) ready.TrySetResult();
                await ready.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
            }
            return result;
        }
    }

    [Theory]
    [InlineData("finalize")]
    [InlineData("payload")]
    [InlineData("amendment")]
    [InlineData("amendment-duplicate")]
    public async Task NotebookLifecycleConcurrentCommandsHaveOneLogicalWinner(string race)
    {
        var (patients, _, _) = await NotebookPatients("Doctor", ["notebook.read", "notebook.write"]);
        using var web = Client(); var enrolled = await Enroll(web);
        using var mobile = Client(false); var token = await MobileComplete(mobile, enrolled.Key);
        var page = await CreateNotebookPageOk(mobile, patients[0].Id, new { title = "Races" });
        var patient = patients[0].Id; var id = page.GetProperty("id").GetGuid(); var version = page.GetProperty("rowVersion").GetString()!;
        bool amendment = race.StartsWith("amendment");
        if (amendment)
        {
            using var finalized = await FinalizeNotebook(mobile, patient, id, version);
            version = (await Json(finalized)).GetProperty("rowVersion").GetString()!;
        }
        var barrier = new NotebookCommandBarrier();
        using var host = _factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddDbContext<ClinicDbContext>(o => o.AddInterceptors(barrier))));
        using var concurrent = host.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        concurrent.DefaultRequestHeaders.Authorization = new("Bearer", token);
        var a = amendment ? UploadNotebook(concurrent, patient, id, version, "a", amendment: true) : FinalizeNotebook(concurrent, patient, id, version);
        var b = race == "finalize" ? FinalizeNotebook(concurrent, patient, id, version)
            : UploadNotebook(concurrent, patient, id, version, race == "amendment-duplicate" ? "a" : "b", amendment: amendment);
        var responses = await Task.WhenAll(a, b);
        if (race == "finalize") Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        else if (race == "amendment-duplicate")
        {
            Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
            Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK);
        }
        else
        {
            Assert.Single(responses, r => r.IsSuccessStatusCode);
            Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Conflict);
        }
        await using var db = _database.CreateContext();
        var persisted = await db.Set<NotebookPage>().SingleAsync();
        var revisionCount = await db.Set<NotebookRevision>().CountAsync();
        var finalCount = await db.AuditEvents.CountAsync(x => x.ActionCode == "notebook.page.finalize");
        var saves = await db.AuditEvents.CountAsync(x => x.ActionCode == "notebook.revision.save" || x.ActionCode == "notebook.revision.amend");
        Assert.Equal(revisionCount - 1, saves);
        Assert.Equal(saves, await db.Set<StoredFile>().CountAsync());
        Assert.Equal(persisted.CurrentRevisionNumber, revisionCount);
        Assert.Equal(persisted.FinalizedAtUtc is null ? 0 : 1, finalCount);
        if (race == "payload") Assert.Equal(1, finalCount + saves);
        else Assert.Equal(1, finalCount);
        if (amendment) Assert.Equal(1, saves);
        foreach (var response in responses) response.Dispose();
    }
}
