namespace Clinic.Application.Storage;

/// <summary>
/// Provider-neutral private storage for confidential clinical files. Every object lives behind
/// an opaque server-generated key; there are no public URLs, no anonymous access and no
/// provider-specific types in this contract. Implementations must keep keys contained beneath
/// the private store root and must never derive anything from client-supplied names.
/// </summary>
public interface IFileStorage
{
    /// <summary>
    /// Streams content into non-final staging inside the private store while computing the
    /// exact byte count and SHA-256 in one pass. Enforces the configured absolute size limit
    /// (FileSizeLimitExceededException) and rejects empty payloads (EmptyFileException) based
    /// on the bytes actually read, never a declared length. The staged object is invisible to
    /// OpenReadAsync and is cleaned up by the implementation when this call fails.
    /// </summary>
    Task<StagedObject> StageAsync(Stream content, CancellationToken cancellationToken = default);

    /// <summary>Moves a staged object to its final opaque key. On success the staged location
    /// no longer exists. A failure creates no final object and never touches an existing one;
    /// the staged object may still exist and is the caller's to clean up via DeleteAsync.</summary>
    Task PromoteAsync(string stagedKey, string storageKey, CancellationToken cancellationToken = default);

    /// <summary>Opens the stored bytes for reading, or null when no object exists for the key
    /// (a controlled missing-object result, not an exception).</summary>
    Task<Stream?> OpenReadAsync(string storageKey, CancellationToken cancellationToken = default);

    Task<bool> ExistsAsync(string storageKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// Internal compensation/cleanup primitive for the upload flow (failed persistence,
    /// abandoned staging, test teardown). It is deliberately exposed on the storage contract —
    /// NOT an application-user deletion feature: future clinical attachment lifecycles decide
    /// retention and removal policy independently.
    /// </summary>
    Task DeleteAsync(string storageKey, CancellationToken cancellationToken = default);
}

/// <summary>Content facts measured during staging; callers may not substitute their own.</summary>
public sealed record StagedObject(string StagedKey, string Sha256, long SizeBytes);

/// <summary>Generates the server-side opaque key format shared by all providers.</summary>
public static class FileStorageKey
{
    public const string Prefix = "clinic-files/";
    public const string StagingPrefix = "clinic-files/_staging/";

    public static string NewStorageKey() => $"{Prefix}{Guid.NewGuid():n}";

    /// <summary>Structural shape only; the Domain entity re-validates persisted values.</summary>
    public static bool IsWellFormed(string key) =>
        !string.IsNullOrWhiteSpace(key) && key.Length <= Domain.Entities.StoredFile.MaxStorageKeyLength &&
        (key.StartsWith(Prefix, StringComparison.Ordinal) || key.StartsWith(StagingPrefix, StringComparison.Ordinal)) &&
        key.AsSpan(Prefix.Length).IndexOfAny('/', '\\') < 0;
}
