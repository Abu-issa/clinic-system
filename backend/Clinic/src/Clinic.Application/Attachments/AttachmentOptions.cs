using Clinic.Application.Storage;

namespace Clinic.Application.Attachments;

/// <summary>
/// Feature-level attachment configuration. MaxFileSizeBytes is the attachment-specific limit
/// and must never exceed the storage foundation's FileStorage:MaxFileSizeBytes (startup
/// validation enforces both bounds); the streamed byte count remains the only authoritative
/// size. No other feature-level policy exists yet.
/// </summary>
public sealed class AttachmentOptions
{
    public const string SectionName = "Attachments";
    public const long DefaultMaxFileSizeBytes = 10_485_760; // 10 MiB initial clinical limit.

    public long MaxFileSizeBytes { get; init; } = DefaultMaxFileSizeBytes;

    public bool IsValid() =>
        MaxFileSizeBytes is >= 1 and <= FileStorageOptions.AbsoluteMaxFileSizeBytes;
}
