using Clinic.Domain.Entities;

namespace Clinic.Application.Visits;

public interface IVisitStore
{
    Task<bool> PatientExistsAsync(Guid id, CancellationToken ct);
    Task<bool> DoctorExistsAsync(Guid id, CancellationToken ct);
    Task<VisitAppointmentReference?> AppointmentAsync(Guid id, CancellationToken ct);
    Task<Visit?> GetAsync(Guid patientId, Guid visitId, CancellationToken ct);
    Task<IReadOnlyList<VisitListItem>> ListAsync(Guid patientId, int skip, int take, CancellationToken ct);
    void Add(Visit visit);
    void ExpectVersion(Visit visit, byte[] version);
    Task SaveAsync(CancellationToken ct);
    void DiscardChanges();
}
