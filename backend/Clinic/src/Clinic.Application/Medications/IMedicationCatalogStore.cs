using Clinic.Domain.Entities;

namespace Clinic.Application.Medications;

/// <summary>Two concurrent creates established the same catalog identity (unique index backstop).</summary>
public sealed class MedicationCatalogConflictException : Exception;

public interface IMedicationCatalogStore
{
    Task<Medication?> GetByIdAsync(Guid id, CancellationToken ct);
    Task<(IReadOnlyList<MedicationListItem> Items, int TotalCount)> SearchAsync(MedicationSearchQuery query, CancellationToken ct);
    Task<bool> ExistsAsync(Guid id, CancellationToken ct);
    Task<bool> ExistsDuplicateAsync(Medication candidate, CancellationToken ct);
    void Add(Medication medication);
    void ExpectVersion(Medication medication, byte[] version);
    Task SaveAsync(CancellationToken ct);
    void DiscardChanges();
}
