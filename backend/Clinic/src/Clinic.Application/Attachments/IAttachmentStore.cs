using Clinic.Domain.Entities;

namespace Clinic.Application.Attachments;

/// <summary>Persistence + query contract for the attachment workflow. Upload persistence
/// stages StoredFile + PatientAttachment into the current unit of work so one SaveAsync can
/// commit them together with the file.upload audit event; a failed save commits nothing and
/// DiscardChanges detaches the rejected rows (mirroring the other store contracts).</summary>
public interface IAttachmentStore
{
    Task<bool> PatientExistsAsync(Guid patientId, CancellationToken cancellationToken = default);

    /// <summary>The visit's owning doctor when the visit exists and belongs to the patient;
    /// null otherwise (hidden mismatch).</summary>
    Task<AttachmentVisitReference?> VisitAsync(Guid patientId, Guid visitId, CancellationToken cancellationToken = default);

    /// <summary>Stages the new StoredFile and PatientAttachment rows for the shared save.</summary>
    void Add(StoredFile file, PatientAttachment attachment);

    /// <summary>Resolves a download target inside the patient scope: the attachment's storage
    /// key and display metadata, or null when no such attachment belongs to this patient.</summary>
    Task<AttachmentDownloadReference?> DownloadAsync(Guid patientId, Guid attachmentId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AttachmentListItem>> ListAsync(Guid patientId, Guid? visitId, int page, int pageSize,
        CancellationToken cancellationToken = default);

    Task SaveAsync(CancellationToken cancellationToken = default);

    void DiscardChanges();
}

public sealed record AttachmentVisitReference(Guid VisitId, Guid DoctorId);

/// <summary>Everything a protected download needs, resolved server-side from patient + attachment ID.
/// The storage key stays an opaque token; it is never sent to clients.</summary>
public sealed record AttachmentDownloadReference(
    Guid AttachmentId, Guid StoredFileId, string StorageKey, string OriginalFileName, string ContentType);

/// <summary>List projection: display metadata only — never StorageKey, hash, actor or paths.</summary>
public sealed record AttachmentListItem(
    Guid AttachmentId, Guid? VisitId, string OriginalFileName, string ContentType, long SizeBytes,
    DateTimeOffset CreatedAtUtc);

/// <summary>Created-attachment projection returned by a successful upload (no StorageKey, no hash).</summary>
public sealed record AttachmentCreated(
    Guid AttachmentId, Guid PatientId, Guid? VisitId, string OriginalFileName, string ContentType,
    long SizeBytes, DateTimeOffset CreatedAtUtc);

public enum AttachmentUploadError
{
    /// <summary>The route patient does not exist (hidden result).</summary>
    PatientNotFound = 1,
    /// <summary>The route visit does not exist or belongs to another patient (hidden result).</summary>
    VisitMismatch = 2,
}
