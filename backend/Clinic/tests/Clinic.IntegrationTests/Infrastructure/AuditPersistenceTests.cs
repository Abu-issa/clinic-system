using Clinic.Application.Audit;
using Clinic.Domain.Entities;
using Clinic.Domain.Enums;
using Clinic.Infrastructure.Audit;
using Clinic.Infrastructure.Persistence;
using Clinic.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Clinic.IntegrationTests.Infrastructure;

public sealed class AuditPersistenceTests(SqlDatabaseFixture database) : IClassFixture<SqlDatabaseFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 9, 16, 14, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.MinValue;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static (AuditEventStore Store, FixedClock Clock) Store(ClinicDbContext db)
    {
        var clock = new FixedClock();
        return (new AuditEventStore(db, clock), clock);
    }

    private static AuditAppendRequest Request(
        string action = "prescription.finalize",
        string resourceType = "prescription",
        string? resourceId = null,
        Guid? patientId = null,
        AuditOutcome outcome = AuditOutcome.Succeeded,
        string? actor = "staff-1",
        string? traceId = "trace-0HN7",
        IReadOnlyCollection<KeyValuePair<string, string>>? metadata = null) =>
        new AuditAppendRequest(actor, action, resourceType, resourceId ?? Guid.NewGuid().ToString("N"),
            patientId, outcome, traceId, metadata);

    [Fact]
    public async Task FullRoundTripPreservesEveryField()
    {
        await using var db = database.CreateContext();
        var (store, clock) = Store(db);
        clock.Now = Now;
        var metadata = new Dictionary<string, string> { ["reason-code"] = "wrong-drug", ["item.count"] = "3" };
        var resourceId = Guid.NewGuid().ToString("N");
        store.Append(Request(
            action: "prescription.cancel", resourceId: resourceId, patientId: Guid.NewGuid(),
            outcome: AuditOutcome.Failed, traceId: "trace-42", metadata: metadata));
        await store.SaveAsync();

        await using var verify = database.CreateContext();
        var saved = await verify.AuditEvents.AsNoTracking().SingleAsync(x => x.ResourceId == resourceId);
        Assert.Equal("prescription.cancel", saved.ActionCode);
        Assert.Equal("prescription", saved.ResourceType);
        Assert.Equal(Now, saved.OccurredAtUtc);
        Assert.Equal("staff-1", saved.ActorStaffId);
        Assert.Equal(AuditOutcome.Failed, saved.Outcome);
        Assert.Equal("trace-42", saved.TraceId);
        Assert.Equal("wrong-drug", saved.Metadata["reason-code"]);
        Assert.Equal("3", saved.Metadata["item.count"]);
        Assert.NotNull(saved.PatientId);
    }

    [Fact]
    public async Task NullActorAndNullPatientAndEmptyMetadataRoundTrip()
    {
        await using var db = database.CreateContext();
        var (store, _) = Store(db);
        var resourceId = Guid.NewGuid().ToString("N");
        store.Append(Request(actor: null, resourceId: resourceId, patientId: null, traceId: null, metadata: null));
        await store.SaveAsync();

        await using var verify = database.CreateContext();
        var saved = await verify.AuditEvents.AsNoTracking().SingleAsync(x => x.ResourceId == resourceId);
        Assert.Null(saved.ActorStaffId);
        Assert.Null(saved.PatientId);
        Assert.Null(saved.TraceId);
        Assert.Empty(saved.Metadata);
        Assert.Equal(AuditOutcome.Succeeded, saved.Outcome);
    }

    [Fact]
    public async Task MachineSafeMetadataValuesRoundTripAndProseIsRejectedBeforeSql()
    {
        await using var db = database.CreateContext();
        var (store, _) = Store(db);
        var resourceId = Guid.NewGuid().ToString("N");
        store.Append(Request(resourceId: resourceId,
            metadata: [new("reason-code", "wrong-drug"), new("ratio", "500/125")]));
        await store.SaveAsync();

        // Free-form prose cannot pass the machine-safe value model: it is rejected at Append,
        // before any SQL statement exists.
        Assert.Throws<ArgumentException>(() => store.Append(Request(resourceId: Guid.NewGuid().ToString("N"),
            metadata: [new("note", "patient diagnosed with mild pneumonia")])));

        await using var verify = database.CreateContext();
        var saved = await verify.AuditEvents.AsNoTracking().SingleAsync(x => x.ResourceId == resourceId);
        Assert.Equal("wrong-drug", saved.Metadata["reason-code"]);
        Assert.Equal("500/125", saved.Metadata["ratio"]);
        // The prose attempt never staged a row for its resource id.
        Assert.Equal(1, await verify.AuditEvents.AsNoTracking().CountAsync(x => x.ResourceId == resourceId));
    }

    [Fact]
    public async Task EventTimestampComesFromTheConfiguredClockAndIsNormalizedToUtc()
    {
        await using var db = database.CreateContext();
        var store = new AuditEventStore(db, new OffsetClock());
        var resourceId = Guid.NewGuid().ToString("N");
        store.Append(Request(resourceId: resourceId));
        await store.SaveAsync();

        await using var verify = database.CreateContext();
        var saved = await verify.AuditEvents.AsNoTracking().SingleAsync(x => x.ResourceId == resourceId);
        Assert.Equal(TimeSpan.Zero, saved.OccurredAtUtc.Offset);
        // 17:00+03:00 retains the same instant normalized to 14:00Z.
        Assert.Equal(new DateTimeOffset(2026, 9, 16, 17, 0, 0, TimeSpan.FromHours(3)).ToUniversalTime(),
            saved.OccurredAtUtc);
    }

    private sealed class OffsetClock : TimeProvider
    {
        // A non-UTC DateTimeOffset proves the store retains the instant while normalizing.
        public override DateTimeOffset GetUtcNow() =>
            new DateTimeOffset(2026, 9, 16, 17, 0, 0, TimeSpan.FromHours(3));
    }

    [Fact]
    public async Task MultipleEventsForOneResourceArePreservedIndependentlyInChronologicalOrder()
    {
        await using var db = database.CreateContext();
        var (store, clock) = Store(db);
        var resourceId = Guid.NewGuid().ToString("N");
        var firstAt = new DateTimeOffset(2026, 9, 16, 10, 0, 0, TimeSpan.Zero);
        clock.Now = firstAt;
        store.Append(Request(action: "prescription.create", resourceId: resourceId));
        clock.Now = firstAt.AddHours(1);
        store.Append(Request(action: "prescription.finalize", resourceId: resourceId));
        clock.Now = firstAt.AddHours(2);
        store.Append(Request(action: "prescription.release", resourceId: resourceId));
        await store.SaveAsync();

        await using var verify = database.CreateContext();
        var history = await verify.AuditEvents.AsNoTracking()
            .Where(x => x.ResourceType == "prescription" && x.ResourceId == resourceId)
            .OrderBy(x => x.OccurredAtUtc)
            .ToListAsync();
        Assert.Equal(["prescription.create", "prescription.finalize", "prescription.release"],
            history.Select(x => x.ActionCode).ToArray());
        Assert.Equal([firstAt, firstAt.AddHours(1), firstAt.AddHours(2)],
            history.Select(x => x.OccurredAtUtc).ToArray());
    }

    [Fact]
    public async Task SourceRecordDeletionDoesNotDeleteAuditHistory()
    {
        await using var db = database.CreateContext();
        var (store, _) = Store(db);
        var patient = new Patient("Synthetic audit patient", "audit-phone");
        db.Patients.Add(patient);
        await db.SaveChangesAsync();
        store.Append(Request(action: "patient.record.read", resourceType: "patient",
            resourceId: patient.Id.ToString("N"), patientId: patient.Id));
        await store.SaveAsync();

        // No FK exists between AuditEvents and clinical aggregates, so removing the source row
        // cannot cascade into audit history.
        var source = await db.Set<Patient>().SingleAsync(x => x.Id == patient.Id);
        db.Set<Patient>().Remove(source);
        await db.SaveChangesAsync();

        await using var verify = database.CreateContext();
        Assert.Null(await verify.Patients.AsNoTracking().SingleOrDefaultAsync(x => x.Id == patient.Id));
        Assert.Single(await verify.AuditEvents.AsNoTracking().Where(x => x.PatientId == patient.Id).ToListAsync());
    }

    [Theory]
    [InlineData(EntityState.Modified, true)]
    [InlineData(EntityState.Deleted, true)]
    [InlineData(EntityState.Modified, false)]
    [InlineData(EntityState.Deleted, false)]
    public async Task ModifyingOrDeletingAuditEventsThroughAnySavePathIsRejected(
        EntityState violatingState, bool asynchronous)
    {
        await using var db = database.CreateContext();
        var (store, clock) = Store(db);
        clock.Now = Now;
        var resourceId = Guid.NewGuid().ToString("N");
        store.Append(Request(resourceId: resourceId));
        await store.SaveAsync();

        var auditEvent = await db.AuditEvents.SingleAsync(x => x.ResourceId == resourceId);
        db.Entry(auditEvent).State = violatingState;

        if (asynchronous)
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        else
            Assert.Throws<InvalidOperationException>(() => db.SaveChanges());

        // The row is intact and unchanged after the rejected operation.
        await using var verify = database.CreateContext();
        var persisted = await verify.AuditEvents.AsNoTracking().SingleAsync(x => x.ResourceId == resourceId);
        Assert.Equal(Now, persisted.OccurredAtUtc);
        Assert.Equal("staff-1", persisted.ActorStaffId);
    }

    [Fact]
    public async Task AddedAuditEventsRemainInsertableAndUnrelatedChangesAreNotBlocked()
    {
        await using var db = database.CreateContext();
        var (store, clock) = Store(db);
        clock.Now = Now;

        // An unrelated aggregate mutation plus a fresh audit append share one save.
        var patient = new Patient("Synthetic append-only patient", "append-only-phone");
        db.Patients.Add(patient);
        store.Append(Request(action: "patient.create", resourceType: "patient",
            resourceId: patient.Id.ToString("N"), patientId: patient.Id));
        await db.SaveChangesAsync();

        // A second, unrelated aggregate mutation saves normally alongside another audit append.
        patient.UpdateContactDetails("Renamed synthetic patient", "append-only-phone");
        store.Append(Request(action: "patient.update", resourceType: "patient",
            resourceId: patient.Id.ToString("N"), patientId: patient.Id));
        await db.SaveChangesAsync();

        await using var verify = database.CreateContext();
        var saved = await verify.Patients.AsNoTracking().SingleAsync(x => x.Id == patient.Id);
        Assert.Equal("Renamed synthetic patient", saved.FullName);
        Assert.Equal(2, await verify.AuditEvents.AsNoTracking().CountAsync(x => x.PatientId == patient.Id));
    }

    [Fact]
    public async Task SameTransactionCouplesMutationWithItsAuditEvent()
    {
        // Phase 2 pattern preview: a mutation and its audit event share one unit of work, so
        // both commit together - or a failure would roll both back.
        await using var db = database.CreateContext();
        var (store, _) = Store(db);
        var patient = new Patient("Synthetic audit transaction patient", "audit-tx-phone");
        db.Patients.Add(patient);
        store.Append(Request(action: "patient.create", resourceType: "patient",
            resourceId: patient.Id.ToString("N"), patientId: patient.Id));
        await db.SaveChangesAsync();

        await using var verify = database.CreateContext();
        Assert.NotNull(await verify.Patients.AsNoTracking().SingleOrDefaultAsync(x => x.Id == patient.Id));
        Assert.NotNull(await verify.AuditEvents.AsNoTracking().SingleOrDefaultAsync(x => x.PatientId == patient.Id));
    }

    [Fact]
    public async Task FailedAppendSaveDetachesStagedAuditEventsWithoutCommitting()
    {
        await using var db = database.CreateContext();
        var (store, _) = Store(db);
        var resourceId = Guid.NewGuid().ToString("N");
        store.Append(Request(resourceId: resourceId));
        var staged = db.ChangeTracker.Entries<AuditEvent>().Single();

        // Force a primary-key collision at save time with a pre-inserted row.
        var conflictingId = staged.Entity.Id;
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO AuditEvents (Id, OccurredAtUtc, ActionCode, ResourceType, ResourceId, PatientId, Outcome, TraceId, MetadataJson) " +
            "VALUES ({0}, SYSUTCDATETIME(), 'prescription.finalize', 'prescription', {1}, NULL, 0, NULL, '{{}}')",
            conflictingId, Guid.NewGuid().ToString("N"));
        staged.Property(x => x.Id).CurrentValue = conflictingId;

        await Assert.ThrowsAnyAsync<Exception>(() => store.SaveAsync());

        // The staged event was detached, not retried: only the pre-inserted row exists.
        await using var verify = database.CreateContext();
        Assert.Null(await verify.AuditEvents.AsNoTracking().SingleOrDefaultAsync(x => x.ResourceId == resourceId));
        Assert.Equal(1, await verify.AuditEvents.AsNoTracking().CountAsync(x => x.Id == conflictingId));
    }
}
