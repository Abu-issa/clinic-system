using Clinic.Domain.Entities;

namespace Clinic.Application.Storage;

public sealed record StoreFileRequest(
    Stream Content,
    string OriginalFileName,
    string ContentType,
    string? ActorStaffId);

/// <summary>Metadata reference for a stored file. Deliberately excludes provider paths and
/// storage internals; callers use StorageKey only as an opaque token.</summary>
public sealed record StoredFileResult(
    Guid Id,
    string StorageKey,
    string OriginalFileName,
    string ContentType,
    long SizeBytes,
    string Sha256,
    DateTimeOffset CreatedAtUtc,
    string? CreatedByStaffId);

/// <summary>
/// Minimum reusable flow that stores bytes privately and persists their metadata with a
/// documented compensation pattern (see docs/file-storage-phase-1.md):
/// 1. StageAsync streams bytes into non-final staging, measuring size and SHA-256.
/// 2. The final opaque key and StoredFile are created (server-generated facts only).
/// 3. The object is promoted to its final key.
/// 4. Metadata is committed.
/// Metadata is therefore written only while its object already exists; if step 4 fails the
/// object is deleted as compensation. Storage failures before step 4 leave no metadata and no
/// final object. A crash between promotion and commit can leave an orphaned object without
/// metadata (harmless; future operational reconciliation), never metadata without an object
/// except when the compensating delete itself fails (documented residual risk).
/// </summary>
public sealed class StoredFileService(IFileStorage storage, IStoredFileStore store, TimeProvider clock)
{
    public async Task<StoredFileResult> StoreAsync(StoreFileRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Content);

        var staged = await storage.StageAsync(request.Content, cancellationToken);
        try
        {
            var key = FileStorageKey.NewStorageKey();
            var file = new StoredFile(key, request.OriginalFileName, request.ContentType,
                staged.SizeBytes, staged.Sha256, request.ActorStaffId, clock.GetUtcNow());
            await storage.PromoteAsync(staged.StagedKey, key, cancellationToken);
            try
            {
                await store.SaveAsync(file, cancellationToken);
            }
            catch
            {
                // Metadata commit failed (single-row SaveChanges: nothing was committed), so
                // remove the already-promoted object and leave neither metadata nor file.
                // If this delete also fails, an orphaned object remains (no dangling metadata)
                // and the original failure propagates.
                try { await storage.DeleteAsync(key, CancellationToken.None); }
                catch { /* compensation failure documented as residual risk */ }
                throw;
            }
            return new(file.Id, file.StorageKey, file.OriginalFileName, file.ContentType,
                file.SizeBytes, file.Sha256, file.CreatedAtUtc, file.CreatedByStaffId);
        }
        finally
        {
            // StageAsync succeeded but promotion may not have run (or may have failed before
            // the move): best-effort cleanup of the staged object either way.
            try { await storage.DeleteAsync(staged.StagedKey, CancellationToken.None); }
            catch { /* staged cleanup is best-effort; promoted objects are compensated above */ }
        }
    }
}
