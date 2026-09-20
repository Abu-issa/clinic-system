using Clinic.Domain.Entities;

namespace Clinic.Application.Patients;

public interface IPatientRecordsStore
{
    Task<IReadOnlyList<PatientContextItem>> SearchContextAsync(string term, IReadOnlyCollection<Guid> allowedPatients,
        int skip, int take, CancellationToken ct);
    Task<Patient?> PatientAsync(Guid id, CancellationToken ct);
    Task<PatientMedicalProfile?> ProfileAsync(Guid patientId, CancellationToken ct);
    void Add(Patient patient);
    void Add(PatientMedicalProfile profile);
    void ExpectVersion(Patient patient, byte[] version);
    void ExpectVersion(PatientMedicalProfile profile, byte[] version);
    Task SaveAsync(CancellationToken ct);
    void DiscardChanges();
}

public sealed class PatientRecordConflictException : Exception;
