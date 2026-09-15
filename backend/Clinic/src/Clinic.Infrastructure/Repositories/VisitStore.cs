using Clinic.Application.Visits;
using Clinic.Domain.Entities;
using Clinic.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Clinic.Infrastructure.Repositories;

public sealed class VisitStore(ClinicDbContext db) : IVisitStore
{
    public Task<bool> PatientExistsAsync(Guid id, CancellationToken ct) => db.Patients.AnyAsync(x => x.Id == id, ct);
    public Task<bool> DoctorExistsAsync(Guid id, CancellationToken ct) => db.Doctors.AnyAsync(x => x.Id == id, ct);
    public Task<VisitAppointmentReference?> AppointmentAsync(Guid id, CancellationToken ct) => db.Appointments.AsNoTracking()
        .Where(x => x.Id == id).Select(x => new VisitAppointmentReference(x.PatientId, x.DoctorId)).SingleOrDefaultAsync(ct);
    public Task<Visit?> GetAsync(Guid patientId, Guid visitId, CancellationToken ct)
    {
        var tracked = db.ChangeTracker.Entries<Visit>().SingleOrDefault(x => x.Entity.Id == visitId && x.Entity.PatientId == patientId);
        if (tracked is not null && tracked.Collections.All(x => x.IsLoaded)) return Task.FromResult<Visit?>(tracked.Entity);
        // Avoid separate root/child queries. Writes still require the root's optimistic version check.
        return db.Set<Visit>().Include(x => x.Amendments).Include(x => x.VitalMeasurements).AsSingleQuery()
            .SingleOrDefaultAsync(x => x.Id == visitId && x.PatientId == patientId, ct);
    }
    public async Task<IReadOnlyList<VisitListItem>> ListAsync(Guid patientId, int skip, int take, CancellationToken ct) =>
        await db.Set<Visit>().AsNoTracking().Where(x => x.PatientId == patientId)
            .OrderByDescending(x => x.OccurredAtUtc).ThenBy(x => x.Id).Skip(skip).Take(take)
            .Select(x => new VisitListItem(x.Id, x.PatientId, x.DoctorId, x.AppointmentId, x.OccurredAtUtc, x.Status)).ToArrayAsync(ct);
    public void Add(Visit visit)
    {
        db.Add(visit);
        foreach (var collection in db.Entry(visit).Collections) collection.IsLoaded = true;
    }
    public void ExpectVersion(Visit visit, byte[] version)
    {
        db.Entry(visit).Property(x => x.RowVersion).OriginalValue = version.ToArray();
        db.Entry(visit).Property(x => x.LastModifiedAtUtc).IsModified = true;
    }
    public async Task SaveAsync(CancellationToken ct)
    {
        try { await db.SaveChangesAsync(ct); }
        catch { DiscardChanges(); throw; }
    }
    public void DiscardChanges()
    {
        foreach (var entry in db.ChangeTracker.Entries().Where(x => x.Entity is Visit or VisitAmendment or VitalMeasurement).ToArray())
            entry.State = EntityState.Detached;
    }
}
