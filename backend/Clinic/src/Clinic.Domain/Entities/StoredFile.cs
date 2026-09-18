using System.Text.RegularExpressions;

namespace Clinic.Domain.Entities;

/// <summary>
/// Reusable private-file metadata: who stored which immutable byte sequence, when. Deliberately
/// decoupled from every clinical aggregate (no Patient/Visit/Prescription foreign keys); future
/// attachment features introduce their own link entities that reference StoredFile. The row is
/// created once after its object is safely stored and is never mutated by the application.
/// </summary>
public sealed partial class StoredFile
{
    public const int MaxStorageKeyLength = 200;
    public const int MaxOriginalFileNameLength = 255;
    public const int MaxContentTypeLength = 255;
    public const int MaxCreatedByStaffIdLength = 450;

    /// <summary>
    /// Server-generated opaque provider-neutral key. Only the application generates keys; they
    /// never derive from client input, so original filenames, names, MRNs or clinical text can
    /// never reach the storage path. The fixed "clinic-files/" prefix keeps every object inside
    /// one reserved namespace beneath the configured private root.
    /// </summary>
    [GeneratedRegex(@"^clinic-files/[0-9a-f]{32}$")]
    private static partial Regex StorageKeyShape();

    /// <summary>SHA-256 digest as 64 lowercase hex characters.</summary>
    [GeneratedRegex(@"^[0-9a-f]{64}$")]
    private static partial Regex Sha256Shape();

    public Guid Id { get; private set; }

    /// <summary>Opaque server-generated storage key; never a client-supplied or derived path.</summary>
    public string StorageKey { get; private set; } = string.Empty;

    /// <summary>Bounded display metadata only; never used for storage path construction.</summary>
    public string OriginalFileName { get; private set; } = string.Empty;

    /// <summary>Bounded declared content type from the caller. Declared, not verified: content
    /// sniffing/malware scanning is a future feature-level concern, so this value must never be
    /// trusted for execution or inline rendering decisions.</summary>
    public string ContentType { get; private set; } = string.Empty;

    /// <summary>Exact streamed byte count; never the declared Content-Length.</summary>
    public long SizeBytes { get; private set; }

    /// <summary>SHA-256 of the stored bytes (lowercase hex), computed while streaming.</summary>
    public string Sha256 { get; private set; } = string.Empty;

    /// <summary>Server-generated event time (TimeProvider-sourced), normalized to UTC.</summary>
    public DateTimeOffset CreatedAtUtc { get; private set; }

    /// <summary>Identity staff/account ID of the storing actor; null only for legitimate
    /// system-originated stores (e.g. generated documents without a staff session).</summary>
    public string? CreatedByStaffId { get; private set; }

    private StoredFile() { } // EF Core

    public StoredFile(
        string storageKey,
        string originalFileName,
        string contentType,
        long sizeBytes,
        string sha256,
        string? createdByStaffId,
        DateTimeOffset createdAtUtc)
    {
        if (string.IsNullOrWhiteSpace(storageKey) || storageKey.Length > MaxStorageKeyLength ||
            !StorageKeyShape().IsMatch(storageKey))
            throw new ArgumentException(
                "Storage key must be the server-generated opaque form 'clinic-files/{32 lowercase hex characters}'.",
                nameof(storageKey));
        if (sizeBytes < 1)
            throw new ArgumentException("Stored files must contain at least one byte.", nameof(sizeBytes));
        if (string.IsNullOrWhiteSpace(sha256) || !Sha256Shape().IsMatch(sha256))
            throw new ArgumentException("SHA-256 must be 64 lowercase hex characters.", nameof(sha256));

        var name = Sanitize(originalFileName, MaxOriginalFileNameLength, nameof(originalFileName));
        var type = Sanitize(contentType, MaxContentTypeLength, nameof(contentType));
        if (createdByStaffId is not null)
        {
            createdByStaffId = createdByStaffId.Trim();
            if (createdByStaffId.Length == 0) createdByStaffId = null;
            else if (createdByStaffId.Length > MaxCreatedByStaffIdLength)
                throw new ArgumentException(
                    $"Creator staff ID must not exceed {MaxCreatedByStaffIdLength} characters.", nameof(createdByStaffId));
        }

        Id = Guid.NewGuid();
        StorageKey = storageKey;
        OriginalFileName = name;
        ContentType = type;
        SizeBytes = sizeBytes;
        Sha256 = sha256;
        CreatedByStaffId = createdByStaffId;
        CreatedAtUtc = createdAtUtc.ToUniversalTime();
    }

    // Display metadata sanitization: bounded, trimmed, control-character-free text. Path
    // separators and odd Unicode are harmless here because this value never constructs a
    // storage path; it is only ever echoed back as attachment metadata.
    private static string Sanitize(string value, int maxLength, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"{name} is required.", name);
        value = value.Trim();
        if (value.Length > maxLength)
            throw new ArgumentException($"{name} must not exceed {maxLength} characters.", name);
        if (value.Any(char.IsControl))
            throw new ArgumentException($"{name} must not contain control characters.", name);
        return value;
    }
}
