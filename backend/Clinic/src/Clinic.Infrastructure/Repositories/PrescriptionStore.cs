using System.Runtime.CompilerServices;
using Clinic.Application.Prescriptions;
using Clinic.Domain.Entities;
using Clinic.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Clinic.Infrastructure.Repositories;

public sealed class PrescriptionStore(ClinicDbContext db) : IPrescriptionStore
{
    public Task<VisitPrescriptionReference?> GetVisitReferenceAsync(Guid visitId, CancellationToken ct) =>
        db.Set<Visit>().AsNoTracking()
            .Where(x => x.Id == visitId)
            .Select(x => new VisitPrescriptionReference(x.PatientId, x.DoctorId, x.Status))
            .SingleOrDefaultAsync(ct);

    public Task<Prescription?> GetAsync(Guid patientId, Guid prescriptionId, CancellationToken ct)
    {
        // Keep a fully loaded aggregate coherent within this unit of work. Re-querying includes
        // otherwise merges new database children into an old tracked parent/version after a race.
        var tracked = db.ChangeTracker.Entries<Prescription>()
            .SingleOrDefault(x => x.Entity.Id == prescriptionId && x.Entity.PatientId == patientId);
        if (tracked is not null && tracked.Collections.All(x => x.IsLoaded))
            return Task.FromResult<Prescription?>(tracked.Entity);
        // Avoid separate root/child queries. Writes still require the root's optimistic version check.
        return db.Set<Prescription>().Include(x => x.Items).AsSingleQuery()
            .SingleOrDefaultAsync(x => x.Id == prescriptionId && x.PatientId == patientId, ct);
    }

    public async Task<IReadOnlyList<PrescriptionListItem>> ListByVisitAsync(Guid patientId, Guid visitId, CancellationToken ct) =>
        await db.Set<Prescription>().AsNoTracking()
            .Where(x => x.PatientId == patientId && x.VisitId == visitId)
            .OrderBy(x => x.CreatedAtUtc).ThenBy(x => x.Id)
            .Select(x => new PrescriptionListItem(
                x.Id, x.VisitId, x.PatientId, x.DoctorId, x.Status, x.Items.Count,
                x.CreatedAtUtc, x.FinalizedAtUtc, x.ReleasedAtUtc, x.CancelledAtUtc))
            .ToArrayAsync(ct);

    public async Task<IReadOnlyList<PrescriptionListItem>> ListByPatientAsync(Guid patientId, int skip, int take, CancellationToken ct) =>
        await db.Set<Prescription>().AsNoTracking()
            .Where(x => x.PatientId == patientId)
            .OrderByDescending(x => x.CreatedAtUtc).ThenBy(x => x.Id)
            .Skip(skip).Take(take)
            .Select(x => new PrescriptionListItem(
                x.Id, x.VisitId, x.PatientId, x.DoctorId, x.Status, x.Items.Count,
                x.CreatedAtUtc, x.FinalizedAtUtc, x.ReleasedAtUtc, x.CancelledAtUtc))
            .ToArrayAsync(ct);

    public Task<Medication?> GetMedicationAsync(Guid medicationId, CancellationToken ct) =>
        db.Medications.SingleOrDefaultAsync(x => x.Id == medicationId, ct);

    public async Task<IMedicationActivityScope> BeginMedicationActivityScopeAsync(CancellationToken ct)
    {
        // The finalization transaction owns the catalog read: update locks (UPDLOCK, ROWLOCK) hold
        // referenced medication rows so a concurrent deactivation either commits before this read
        // (and is observed) or waits until this finalization commits. No other infrastructure.
        var tx = await db.Database.BeginTransactionAsync(ct);
        return new MedicationActivityScope(db, tx);
    }

    private sealed class MedicationActivityScope(ClinicDbContext db, IDbContextTransaction tx) : IMedicationActivityScope
    {
        private bool _completed;

        public async Task<IReadOnlyDictionary<Guid, bool>> ReadActiveStatusAsync(
            IReadOnlyCollection<Guid> medicationIds, CancellationToken ct)
        {
            var ids = medicationIds.Distinct().ToArray();
            if (ids.Length == 0)
                return new Dictionary<Guid, bool>();
            // One typed parameter per id: the OPENJSON list expansion SQL Server generates for
            // interpolated collections fails uniqueidentifier conversion here.
            var placeholders = new string[ids.Length];
            for (var i = 0; i < ids.Length; i++)
                placeholders[i] = $"{{{i}}}";
            var sql = FormattableStringFactory.Create(
                $"SELECT * FROM [Medications] WITH (UPDLOCK, ROWLOCK) WHERE [Id] IN ({string.Join(", ", placeholders)})",
                ids.Cast<object>().ToArray());
            var medications = await db.Medications.FromSql(sql).ToListAsync(ct);
            return medications.ToDictionary(x => x.Id, x => x.IsActive);
        }

        public async Task CommitAsync(CancellationToken ct)
        {
            await tx.CommitAsync(ct);
            _completed = true;
        }

        public async ValueTask DisposeAsync()
        {
            if (!_completed)
                await tx.RollbackAsync();
            await tx.DisposeAsync();
        }
    }

    public void Add(Prescription prescription)
    {
        db.Add(prescription);
        foreach (var collection in db.Entry(prescription).Collections) collection.IsLoaded = true;
    }

    public void ExpectVersion(Prescription prescription, byte[] version)
    {
        db.Entry(prescription).Property(x => x.RowVersion).OriginalValue = version.ToArray();
        // Force an aggregate version change even for child-only updates at the same clock instant.
        db.Entry(prescription).Property(x => x.LastModifiedAtUtc).IsModified = true;
    }

    public async Task SaveAsync(CancellationToken ct)
    {
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 })
        {
            // Replacement-link unique indexes rejected the write (e.g. a concurrent replacement
            // won between the rowversion read and this save). No stored identifiers are exposed.
            DiscardChanges();
            throw new PrescriptionReplacementConflictException();
        }
        catch { DiscardChanges(); throw; }
    }

    public void DiscardChanges()
    {
        // A rejected command must not leave partial aggregate mutations available to a later save.
        foreach (var entry in db.ChangeTracker.Entries().Where(x =>
            x.Entity is Prescription or PrescriptionItem).ToArray())
            entry.State = EntityState.Detached;
    }
}
