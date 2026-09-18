using Clinic.Application.ClinicalTests;
using Clinic.Domain.Entities;
using Clinic.Domain.Enums;
using Clinic.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Clinic.Infrastructure.Repositories;

public sealed class ClinicalTestStore(ClinicDbContext db) : IClinicalTestStore
{
    public Task<bool> PatientExistsAsync(Guid patientId, CancellationToken ct) => db.Patients.AnyAsync(x => x.Id == patientId, ct);
    public Task<Guid?> AssociatedDoctorAsync(string actorStaffId, CancellationToken ct) => db.Users.AsNoTracking()
        .Where(x => x.Id == actorStaffId).Select(x => x.AssociatedDoctorId).SingleOrDefaultAsync(ct);
    public Task<Guid?> VisitDoctorAsync(Guid patientId, Guid visitId, CancellationToken ct) => db.Set<Visit>().AsNoTracking()
        .Where(x => x.Id == visitId && x.PatientId == patientId).Select(x => (Guid?)x.DoctorId).SingleOrDefaultAsync(ct);
    public Task<ClinicalTestDetails?> GetAsync(Guid patientId, Guid requestId, CancellationToken ct) =>
        db.Set<ClinicalTestRequest>().AsNoTracking().Where(x => x.PatientId == patientId && x.Id == requestId)
            .Select(x => new ClinicalTestDetails(x.Id, x.PatientId, x.VisitId, x.Category, x.TestName,
                x.ClinicalInstructions, x.Status, x.RequestedAtUtc)).SingleOrDefaultAsync(ct);
    public async Task<IReadOnlyList<ClinicalTestSummary>> ListAsync(Guid patientId, ClinicalTestCategory? category,
        ClinicalTestStatus? status, int page, int pageSize, CancellationToken ct) =>
        await db.Set<ClinicalTestRequest>().AsNoTracking()
            .Where(x => x.PatientId == patientId && (category == null || x.Category == category) && (status == null || x.Status == status))
            .OrderByDescending(x => x.RequestedAtUtc).ThenByDescending(x => x.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(x => new ClinicalTestSummary(x.Id, x.PatientId, x.VisitId, x.Category, x.TestName, x.Status, x.RequestedAtUtc))
            .ToArrayAsync(ct);
    public void Add(ClinicalTestRequest request) => db.Add(request);
    public async Task SaveAsync(CancellationToken ct) => await db.SaveChangesAsync(ct);
    public void DiscardChanges()
    {
        foreach (var entry in db.ChangeTracker.Entries<ClinicalTestRequest>().Where(x => x.State == EntityState.Added).ToArray())
            entry.State = EntityState.Detached;
    }
}
