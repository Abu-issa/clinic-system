using Clinic.Application.Patients;
using Clinic.Domain.Entities;
using Clinic.Domain.Enums;
using Clinic.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Clinic.Infrastructure.Repositories;

public sealed class PatientRecordsStore(ClinicDbContext db) : IPatientRecordsStore
{
    public async Task<IReadOnlyList<PatientContextItem>> SearchContextAsync(string term,
        IReadOnlyCollection<Guid> allowedPatients, int skip, int take, CancellationToken ct)
    {
        // Contains translates as a literal, parameterized substring (not a caller LIKE pattern).
        // Filter by authorized IDs before pagination; never load a complete clinical aggregate.
        return await db.Patients.AsNoTracking()
            .Where(p => allowedPatients.Contains(p.Id) && (p.FullName.Contains(term) ||
                p.MedicalRecordNumber != null && p.MedicalRecordNumber.Contains(term) ||
                p.LegacyPaperFileNumber != null && p.LegacyPaperFileNumber.Contains(term)))
            .OrderBy(p => p.FullName).ThenBy(p => p.Id).Skip(skip).Take(take)
            .Select(p => new PatientContextItem(p.Id, p.FullName, p.MedicalRecordNumber, p.DateOfBirth,
                db.Set<PatientMedicalProfile>().Where(profile => profile.PatientId == p.Id)
                    .Select(profile => (AllergyStatus?)profile.AllergyStatus).FirstOrDefault() ?? AllergyStatus.Unknown))
            .ToListAsync(ct);
    }

    public Task<Patient?> PatientAsync(Guid id, CancellationToken ct) => db.Patients.SingleOrDefaultAsync(x => x.Id == id, ct);
    public Task<PatientMedicalProfile?> ProfileAsync(Guid patientId, CancellationToken ct)
    {
        // Keep a fully loaded aggregate coherent within this unit of work. Re-querying includes
        // otherwise merges new database children into an old tracked parent/version after a race.
        var tracked = db.ChangeTracker.Entries<PatientMedicalProfile>()
            .SingleOrDefault(x => x.Entity.PatientId == patientId);
        if (tracked is not null && tracked.Collections.All(x => x.IsLoaded))
            return Task.FromResult<PatientMedicalProfile?>(tracked.Entity);
        return db.Set<PatientMedicalProfile>().Include(x => x.Allergies).Include(x => x.ChronicConditions)
            .Include(x => x.Medications).Include(x => x.Surgeries).Include(x => x.FamilyHistory)
            .AsSplitQuery().SingleOrDefaultAsync(x => x.PatientId == patientId, ct);
    }
    public void Add(Patient patient) => db.Patients.Add(patient);
    public void Add(PatientMedicalProfile profile) => db.Add(profile);
    public void ExpectVersion(Patient patient, byte[] version)
    {
        db.Entry(patient).Property(x => x.RowVersion).OriginalValue = version;
        db.Entry(patient).Property(x => x.FullName).IsModified = true;
    }
    public void ExpectVersion(PatientMedicalProfile profile, byte[] version)
    {
        db.Entry(profile).Property(x => x.RowVersion).OriginalValue = version;
        // Force an aggregate version change even for child-only updates at the same clock instant.
        db.Entry(profile).Property(x => x.UpdatedAtUtc).IsModified = true;
    }
    public void DiscardChanges()
    {
        // A rejected command must not leave partial aggregate mutations available to a later save.
        foreach (var entry in db.ChangeTracker.Entries().Where(x =>
            x.Entity is Patient or PatientMedicalProfile or PatientClinicalEntry).ToArray())
            entry.State = EntityState.Detached;
    }
    public async Task SaveAsync(CancellationToken ct)
    {
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 })
        {
            DiscardChanges();
            throw new PatientRecordConflictException();
        }
        catch
        {
            // Includes cancellation and unexpected provider/interceptor failures. A later unit-of-work
            // save must never retry a rejected command's pending mutations implicitly.
            DiscardChanges();
            throw;
        }
    }
}
