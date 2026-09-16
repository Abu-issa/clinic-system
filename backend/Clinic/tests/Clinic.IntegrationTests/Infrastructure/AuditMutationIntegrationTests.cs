using Clinic.Application.Audit;
using Clinic.Application.Medications;
using Clinic.Application.Patients;
using Clinic.Application.Prescriptions;
using Clinic.Application.Visits;
using Clinic.Domain.Entities;
using Clinic.Domain.Enums;
using Clinic.Infrastructure.Audit;
using Clinic.Infrastructure.Persistence;
using Clinic.Infrastructure.Repositories;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace Clinic.IntegrationTests.Infrastructure;

// Phase 2 semantics and rollback checks against isolated ClinicTests_* databases.
public sealed partial class AuditMutationIntegrationTests(SqlDatabaseFixture database) : IClassFixture<SqlDatabaseFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 9, 16, 15, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }

    private (VisitService Visits, PrescriptionService Prescriptions, MedicationCatalogService Catalog,
        PatientRecordsService Patients) Services(ClinicDbContext db)
    {
        var clock = new FixedClock();
        var store = new AuditEventStore(db, clock);
        return (
            new VisitService(new VisitStore(db), clock, store),
            new PrescriptionService(new PrescriptionStore(db), clock, store),
            new MedicationCatalogService(new MedicationCatalogStore(db), clock, store),
            new PatientRecordsService(new PatientRecordsStore(db), clock, store));
    }

    private static async Task<(Patient Patient, Doctor Doctor, Visit Visit)> SeedVisitAsync(ClinicDbContext db)
    {
        var patient = new Patient("Audit Visit Patient", "audit-visit-phone");
        var doctor = new Doctor("Audit Visit Doctor");
        db.AddRange(patient, doctor);
        var visit = new Visit(patient.Id, doctor.Id, null, Now, "audit-seed", Now);
        db.Add(visit);
        await db.SaveChangesAsync();
        return (patient, doctor, visit);
    }

    private static async Task<Medication> SeedMedicationAsync(ClinicDbContext db, string prefix)
    {
        var medication = new Medication($"{prefix}-{Guid.NewGuid():N}", null, null, null, "500", "mg",
            DosageForm.Tablet, MedicationRoute.Oral, null, "audit-seed", Now);
        db.Medications.Add(medication);
        await db.SaveChangesAsync();
        return medication;
    }

    [Fact]
    public async Task VisitFinalizeAppendsOneAtomicAuditEvent()
    {
        await using var db = database.CreateContext();
        var s = Services(db);
        var (_, _, visit) = await SeedVisitAsync(db);
        var visitRowVersion = visit.RowVersion.ToArray();

        var result = await s.Visits.FinalizeAsync(visit.PatientId, visit.Id, visitRowVersion, "audit-doctor");
        Assert.True(result.IsSuccess, result.Error.ToString());

        await using var verify = database.CreateContext();
        var events = await verify.Set<AuditEvent>().AsNoTracking()
            .Where(x => x.ResourceType == "visit" && x.ResourceId == visit.Id.ToString("N")).ToListAsync();
        var auditEvent = Assert.Single(events);
        Assert.Equal("visit.finalize", auditEvent.ActionCode);
        Assert.Equal(visit.PatientId, auditEvent.PatientId);
        Assert.Equal("audit-doctor", auditEvent.ActorStaffId);
        Assert.Equal(AuditOutcome.Succeeded, auditEvent.Outcome);
        Assert.Equal(Now, auditEvent.OccurredAtUtc);
        Assert.Empty(auditEvent.Metadata);
        // The mutation itself committed.
        Assert.Equal(VisitStatus.Finalized,
            (await verify.Set<Visit>().AsNoTracking().SingleAsync(x => x.Id == visit.Id)).Status);
    }

    [Fact]
    public async Task VisitAmendAppendsNoClinicalMetadata()
    {
        await using var db = database.CreateContext();
        var s = Services(db);
        var (_, _, visit) = await SeedVisitAsync(db);
        await s.Visits.FinalizeAsync(visit.PatientId, visit.Id, visit.RowVersion.ToArray(), "audit-doctor");
        var finalized = await db.Set<Visit>().AsNoTracking().SingleAsync(x => x.Id == visit.Id);

        var result = await s.Visits.AddAmendmentAsync(visit.PatientId, visit.Id,
            new("amendment reason TEXT", "amendment body TEXT", finalized.RowVersion.ToArray()), "audit-doctor");
        Assert.True(result.IsSuccess, result.Error.ToString());

        await using var verify = database.CreateContext();
        var auditEvent = await verify.Set<AuditEvent>().AsNoTracking()
            .SingleAsync(x => x.ResourceType == "visit" && x.ResourceId == visit.Id.ToString("N") && x.ActionCode == "visit.amend");
        Assert.Empty(auditEvent.Metadata);
        Assert.Equal(visit.PatientId, auditEvent.PatientId);
        Assert.Equal("audit-doctor", auditEvent.ActorStaffId);
        Assert.Equal(AuditOutcome.Succeeded, auditEvent.Outcome);
        Assert.Single((await verify.Set<Visit>().Include(x => x.Amendments).SingleAsync(x => x.Id == visit.Id)).Amendments);
        Assert.DoesNotContain("amendment reason TEXT", Clinic.Infrastructure.Audit.AuditEventStore.SerializeMetadata(auditEvent.Metadata));
        Assert.DoesNotContain("amendment body TEXT", Clinic.Infrastructure.Audit.AuditEventStore.SerializeMetadata(auditEvent.Metadata));
    }

    [Fact]
    public async Task ConcurrentFinalizeFailurePersistsNoMutationAndNoAuditEvent()
    {
        await using var db = database.CreateContext();
        var s = Services(db);
        var (_, _, visit) = await SeedVisitAsync(db);
        var staleVersion = visit.RowVersion.ToArray();
        await s.Visits.UpdateAsync(visit.PatientId, visit.Id,
            new(new VisitClinicalContent(null, null, "moved on", null, null, null, null), staleVersion), "audit-doctor");
        var current = await db.Set<Visit>().AsNoTracking().SingleAsync(x => x.Id == visit.Id);

        var result = await s.Visits.FinalizeAsync(visit.PatientId, visit.Id, staleVersion, "audit-doctor");
        Assert.Equal(VisitError.VisitChanged, result.Error);

        await using var verify = database.CreateContext();
        Assert.Equal(0, await verify.Set<AuditEvent>().CountAsync(
            x => x.ResourceType == "visit" && x.ResourceId == visit.Id.ToString("N")));
        Assert.Equal(VisitStatus.Draft, (await verify.Set<Visit>().AsNoTracking().SingleAsync(x => x.Id == visit.Id)).Status);
        Assert.True((await s.Visits.FinalizeAsync(visit.PatientId, visit.Id, current.RowVersion, "audit-doctor")).IsSuccess);
        Assert.Single(await verify.Set<AuditEvent>().Where(x => x.ResourceId == visit.Id.ToString("N")).ToListAsync());
    }

    [Fact]
    public async Task MedicationLifecycleAppendsCreateUpdateDeactivateActivate()
    {
        await using var db = database.CreateContext();
        var s = Services(db);
        var created = (await s.Catalog.CreateAsync(
            new("AuditMed", null, null, null, "500", "mg", DosageForm.Tablet, MedicationRoute.Oral, null),
            "audit-admin")).Details!;
        var deactivated = (await s.Catalog.DeactivateAsync(created.Id, created.RowVersion, "audit-admin")).Details!;
        var activated = (await s.Catalog.ActivateAsync(created.Id, deactivated.RowVersion, "audit-admin")).Details!;
        await s.Catalog.UpdateAsync(created.Id,
            new("AuditMed", null, null, null, "400", "mg", DosageForm.Tablet, MedicationRoute.Oral, null, activated.RowVersion),
            "audit-admin");

        await using var verify = database.CreateContext();
        var events = await verify.Set<AuditEvent>().AsNoTracking()
            .Where(x => x.ResourceType == "medication" && x.ResourceId == created.Id.ToString("N"))
            .ToListAsync();
        Assert.Equal(new[] { "medication.create", "medication.deactivate", "medication.activate", "medication.update" }.Order(),
            events.Select(x => x.ActionCode).Order());
        Assert.All(events, x =>
        {
            Assert.Null(x.PatientId);
            Assert.Equal("audit-admin", x.ActorStaffId);
            Assert.Equal(AuditOutcome.Succeeded, x.Outcome);
            Assert.Empty(x.Metadata);
        });
        var deactivate = events.Single(x => x.ActionCode == "medication.deactivate");
        Assert.Empty(deactivate.Metadata);
        var update = events.Single(x => x.ActionCode == "medication.update");
        Assert.Empty(update.Metadata);
        // No medication names or strengths leak into the audit trail.
        Assert.DoesNotContain("AuditMed", string.Join("|", events.Select(x => Clinic.Infrastructure.Audit.AuditEventStore.SerializeMetadata(x.Metadata))));
    }

    [Fact]
    public async Task MedicationStaleVersionPersistsNoMutationAndNoAuditEvent()
    {
        await using var db = database.CreateContext();
        var s = Services(db);
        var created = (await s.Catalog.CreateAsync(
            new("AuditStale", null, null, null, "500", "mg", DosageForm.Tablet, MedicationRoute.Oral, null),
            "audit-admin")).Details!;
        var stale = created.RowVersion.ToArray();
        await s.Catalog.DeactivateAsync(created.Id, stale, "audit-admin");

        var result = await s.Catalog.DeactivateAsync(created.Id, stale, "audit-admin");
        Assert.Equal(MedicationCatalogError.MedicationChanged, result.Error);

        await using var verify = database.CreateContext();
        Assert.Equal(1, await verify.Set<AuditEvent>().CountAsync(
            x => x.ResourceType == "medication" && x.ResourceId == created.Id.ToString("N")
            && x.ActionCode == "medication.deactivate"));
    }

    [Fact]
    public async Task PrescriptionLifecycleAppendsOneEventPerOperation()
    {
        await using var db = database.CreateContext();
        var s = Services(db);
        var (_, _, visit) = await SeedVisitAsync(db);
        var medication = await SeedMedicationAsync(db, "AuditRx");

        var draft = (await s.Prescriptions.CreateDraftAsync(new(visit.Id), "audit-doctor")).Details!;
        draft = (await s.Prescriptions.AddItemAsync(visit.PatientId, draft.Id,
            new(medication.Id, "1 tablet", "twice daily", "7 days", null, null, draft.RowVersion), "audit-doctor")).Details!;
        draft = (await s.Prescriptions.UpdateItemAsync(visit.PatientId, draft.Id, draft.Items.Single().Id,
            new(null, "2 tablets", "daily", "5 days", null, null, draft.RowVersion), "audit-doctor")).Details!;
        draft = (await s.Prescriptions.UpdateDraftNotesAsync(visit.PatientId, draft.Id,
            new("notes", draft.RowVersion), "audit-doctor")).Details!;
        draft = (await s.Prescriptions.ReorderItemsAsync(visit.PatientId, draft.Id,
            new([draft.Items.Single().Id], draft.RowVersion), "audit-doctor")).Details!;
        var finalized = (await s.Prescriptions.FinalizeAsync(visit.PatientId, draft.Id, draft.RowVersion, "audit-doctor")).Details!;
        var released = (await s.Prescriptions.ReleaseAsync(visit.PatientId, draft.Id, finalized.RowVersion, "audit-doctor")).Details!;
        var cancelled = (await s.Prescriptions.CancelAsync(visit.PatientId, draft.Id,
            new("audit reason TEXT", released.RowVersion), "audit-doctor")).Details!;

        await using var verify = database.CreateContext();
        var events = await verify.Set<AuditEvent>().AsNoTracking()
            .Where(x => x.ResourceType == "prescription" && x.ResourceId == draft.Id.ToString("N"))
            .ToListAsync();
        Assert.Equal(new[] { "prescription.create", "prescription.item.add", "prescription.item.update", "prescription.notes.update",
            "prescription.item.reorder", "prescription.finalize", "prescription.release", "prescription.cancel" }.Order(),
            events.Select(x => x.ActionCode).Order());
        Assert.All(events, x =>
        {
            Assert.Equal(visit.PatientId, x.PatientId);
            Assert.Equal("audit-doctor", x.ActorStaffId);
            Assert.Equal(AuditOutcome.Succeeded, x.Outcome);
        });
        var finalize = events.Single(x => x.ActionCode == "prescription.finalize");
        Assert.Equal("1", finalize.Metadata["item.count"]);
        var cancel = events.Single(x => x.ActionCode == "prescription.cancel");
        Assert.Equal(PrescriptionStatus.Cancelled,
            (await verify.Set<Prescription>().SingleAsync(x => x.Id == draft.Id)).Status);
        // Cancellation free text never enters the audit trail.
        Assert.DoesNotContain("audit reason TEXT", Clinic.Infrastructure.Audit.AuditEventStore.SerializeMetadata(cancel.Metadata));
    }

    [Fact]
    public async Task ConcurrentFinalizeLoserPersistsNoAuditEventAndRetrySucceeds()
    {
        await using var db = database.CreateContext();
        var s = Services(db);
        var (_, _, visit) = await SeedVisitAsync(db);
        var medication = await SeedMedicationAsync(db, "AuditLoser");
        var draft = (await s.Prescriptions.CreateDraftAsync(new(visit.Id), "audit-doctor")).Details!;
        draft = (await s.Prescriptions.AddItemAsync(visit.PatientId, draft.Id,
            new(medication.Id, "1 tablet", "twice daily", "7 days", null, null, draft.RowVersion), "audit-doctor")).Details!;
        var staleVersion = draft.RowVersion.ToArray();
        draft = (await s.Prescriptions.UpdateDraftNotesAsync(visit.PatientId, draft.Id,
            new("concurrent change", staleVersion), "audit-doctor")).Details!;

        var loser = await s.Prescriptions.FinalizeAsync(visit.PatientId, draft.Id, staleVersion, "audit-doctor");
        Assert.Equal(PrescriptionError.PrescriptionChanged, loser.Error);

        await using var verify = database.CreateContext();
        Assert.Null(await verify.Set<AuditEvent>().AsNoTracking().SingleOrDefaultAsync(
            x => x.ActionCode == "prescription.finalize" && x.ResourceId == draft.Id.ToString("N")));

        var retry = await s.Prescriptions.FinalizeAsync(visit.PatientId, draft.Id, draft.RowVersion, "audit-doctor");
        Assert.True(retry.IsSuccess, retry.Error.ToString());
        Assert.Equal(1, await verify.Set<AuditEvent>().CountAsync(
            x => x.ActionCode == "prescription.finalize" && x.ResourceId == draft.Id.ToString("N")));
    }

    [Fact]
    public async Task ReplacementAppendsReplaceEventWithOpaqueLink()
    {
        await using var db = database.CreateContext();
        var s = Services(db);
        var (_, _, visit) = await SeedVisitAsync(db);
        var medication = await SeedMedicationAsync(db, "AuditRepl");
        var draft = (await s.Prescriptions.CreateDraftAsync(new(visit.Id), "audit-doctor")).Details!;
        draft = (await s.Prescriptions.AddItemAsync(visit.PatientId, draft.Id,
            new(medication.Id, "1 tablet", "twice daily", "7 days", null, null, draft.RowVersion), "audit-doctor")).Details!;
        var finalized = (await s.Prescriptions.FinalizeAsync(visit.PatientId, draft.Id, draft.RowVersion, "audit-doctor")).Details!;
        var cancelled = (await s.Prescriptions.CancelAsync(visit.PatientId, draft.Id,
            new("wrong drug", finalized.RowVersion), "audit-doctor")).Details!;

        var replacement = await s.Prescriptions.CreateDraftAsync(
            new(visit.Id, null, draft.Id, cancelled.RowVersion), "audit-doctor");
        Assert.True(replacement.IsSuccess, replacement.Error.ToString());

        await using var verify = database.CreateContext();
        var replaceEvent = await verify.Set<AuditEvent>().AsNoTracking().SingleAsync(
            x => x.ActionCode == "prescription.replace" && x.ResourceId == replacement.Details!.Id.ToString("N"));
        Assert.Equal(draft.Id.ToString("N"), replaceEvent.Metadata["replaces-prescription"]);
        Assert.Equal(visit.PatientId, replaceEvent.PatientId);
        Assert.Equal("audit-doctor", replaceEvent.ActorStaffId);
        Assert.Equal(AuditOutcome.Succeeded, replaceEvent.Outcome);
        Assert.Equal(replacement.Details!.Id,
            (await verify.Set<Prescription>().SingleAsync(x => x.Id == draft.Id)).ReplacedByPrescriptionId);
        Assert.Single(await verify.Set<AuditEvent>().Where(x => x.ResourceId == replacement.Details.Id.ToString("N")).ToListAsync());
    }

    [Fact]
    public async Task PatientMutationAppendsAdminEvents()
    {
        await using var db = database.CreateContext();
        var s = Services(db);
        var created = (await s.Patients.CreateAsync(
            new("Audit Patient", "audit-phone", null, "AUDIT-MRN-1", null, null, null, null, null),
            "audit-admin")).Details!;

        var updated = await s.Patients.UpdateAsync(created.PatientId,
            new("Audit Patient Updated", "audit-phone", null, null, null, null, created.RowVersion),
            "audit-admin");
        Assert.True(updated.IsSuccess, updated.Error.ToString());

        await using var verify = database.CreateContext();
        var events = await verify.Set<AuditEvent>().AsNoTracking()
            .Where(x => x.ResourceType == "patient" && x.ResourceId == created.PatientId.ToString("N")).ToListAsync();
        Assert.Equal(["patient.admin.update", "patient.create"], events.Select(x => x.ActionCode).Order().ToArray());
        Assert.All(events, x =>
        {
            Assert.Equal("audit-admin", x.ActorStaffId);
            Assert.Equal(created.PatientId, x.PatientId);
            Assert.Equal(AuditOutcome.Succeeded, x.Outcome);
            Assert.Empty(x.Metadata);
        });
        Assert.Equal("Audit Patient Updated", (await verify.Patients.SingleAsync(x => x.Id == created.PatientId)).FullName);
    }

    [Fact]
    public async Task AuditInsertFailureRollsBackTheCoupledMutation()
    {
        // If the audit INSERT fails at the database boundary, the coupled mutation must not
        // commit. A command interceptor on a dedicated context (same isolated database) fails
        // INSERTs targeting AuditEvents; no production code is touched.
        var interceptor = new FailingAuditInsertInterceptor();
        var options = new DbContextOptionsBuilder<ClinicDbContext>()
            .UseSqlServer(database.ConnectionString)
            .AddInterceptors(interceptor)
            .Options;
        await using var db = new ClinicDbContext(options);
        var clock = new FixedClock();
        var store = new AuditEventStore(db, clock);
        var visits = new VisitService(new VisitStore(db), clock, store);
        var (_, _, visit) = await SeedVisitAsync(db);

        interceptor.Enabled = true;
        // Fail-closed: the coupled save throws together with the audit insert rather than
        // committing the mutation without its audit event.
        var error = await Assert.ThrowsAsync<DbUpdateException>(() =>
            visits.FinalizeAsync(visit.PatientId, visit.Id, visit.RowVersion.ToArray(), "audit-doctor"));
        Assert.IsType<AuditInsertFailureException>(error.InnerException);
        interceptor.Enabled = false;

        await using var verify = database.CreateContext();
        // The coupled mutation rolled back: the visit remains Draft with no audit event.
        var saved = await verify.Set<Visit>().AsNoTracking().SingleAsync(x => x.Id == visit.Id);
        Assert.Equal(VisitStatus.Draft, saved.Status);
        Assert.Null(saved.FinalizedAtUtc);
        Assert.Null(await verify.Set<AuditEvent>().AsNoTracking().SingleOrDefaultAsync(
            x => x.ResourceId == visit.Id.ToString("N") && x.ActionCode == "visit.finalize"));
    }

    private sealed class FailingAuditInsertInterceptor : DbCommandInterceptor
    {
        public bool Enabled { get; set; }

        public override InterceptionResult<int> NonQueryExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
        {
            FailIfAuditInsert(command);
            return base.NonQueryExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            FailIfAuditInsert(command);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            FailIfAuditInsert(command);
            return base.ReaderExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            FailIfAuditInsert(command);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        private void FailIfAuditInsert(DbCommand command)
        {
            if (Enabled && command.CommandText.Contains("INSERT INTO [AuditEvents]", StringComparison.OrdinalIgnoreCase))
                throw new AuditInsertFailureException();
        }
    }

    private sealed class AuditInsertFailureException : Exception;
}
