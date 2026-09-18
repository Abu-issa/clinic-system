using Clinic.Domain.Entities;

namespace Clinic.Application.Storage;

/// <summary>Persistence contract for StoredFile metadata. Save persists exactly one newly
/// created row atomically: a failed SaveAsync leaves no committed row, so upload compensation
/// only ever needs to remove the promoted object. No update or delete operations exist —
/// stored-file rows are created once and never mutated by the application, and no application
/// hard-delete API exists for clinical content (future attachment lifecycles decide retention).</summary>
public interface IStoredFileStore
{
    /// <summary>Persists new metadata. Throws on persistence failure without any committed row.</summary>
    Task SaveAsync(StoredFile file, CancellationToken cancellationToken = default);
}
