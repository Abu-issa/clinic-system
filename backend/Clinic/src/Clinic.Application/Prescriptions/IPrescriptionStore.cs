using Clinic.Domain.Entities;

namespace Clinic.Application.Prescriptions;

/// <summary>
/// The replacement link unique indexes rejected the save: the original already has a replacement
/// (or a replacement is already claimed by an original) despite the optimistic-concurrency checks.
/// Mapped to ReplacementMismatch without leaking stored identifiers.
/// </summary>
public sealed class PrescriptionReplacementConflictException : Exception;

/// <summary>
/// A transaction-spanning read scope over catalog activity used at finalization. Reads take
/// update locks on the referenced medication rows so a deactivation cannot commit between the
/// eligibility check and the finalization save: it either commits before the read (and is seen)
/// or waits until the finalization commits. Disposing without committing rolls the scope back.
/// </summary>
public interface IMedicationActivityScope : IAsyncDisposable
{
    Task<IReadOnlyDictionary<Guid, bool>> ReadActiveStatusAsync(IReadOnlyCollection<Guid> medicationIds, CancellationToken ct);
    Task CommitAsync(CancellationToken ct);
}

public interface IPrescriptionStore
{
    Task<VisitPrescriptionReference?> GetVisitReferenceAsync(Guid visitId, CancellationToken ct);    Task<Prescription?> GetAsync(Guid patientId, Guid prescriptionId, CancellationToken ct);
    Task<IReadOnlyList<PrescriptionListItem>> ListByVisitAsync(Guid patientId, Guid visitId, CancellationToken ct);
    Task<IReadOnlyList<PrescriptionListItem>> ListByPatientAsync(Guid patientId, int skip, int take, CancellationToken ct);
    Task<Medication?> GetMedicationAsync(Guid medicationId, CancellationToken ct);

    /// <summary>Opens the transactional catalog-activity scope used by finalization.</summary>
    Task<IMedicationActivityScope> BeginMedicationActivityScopeAsync(CancellationToken ct);

    void Add(Prescription prescription);
    void ExpectVersion(Prescription prescription, byte[] version);
    Task SaveAsync(CancellationToken ct);
    void DiscardChanges();
}
