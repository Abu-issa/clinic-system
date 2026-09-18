namespace Clinic.Domain.Entities;

/// <summary>
/// The clinical visibility link: a StoredFile becomes a patient's clinical attachment only
/// through exactly one PatientAttachment row. Metadata about the bytes stays on StoredFile
/// (never duplicated here). Create-once like StoredFile: the application has no update or
/// delete surface — retention/removal is an unresolved policy. A StoredFile can be linked to
/// at most one patient (enforced by a unique StoredFileId index); workflows that genuinely
/// need the same bytes twice store another StoredFile instead of creating ambiguous
/// cross-patient authorization.
/// </summary>
public sealed class PatientAttachment
{
    public const int MaxCreatedByStaffIdLength = 450;

    public Guid Id { get; private set; }

    /// <summary>The owning patient; required. Download authorization resolves through it.</summary>
    public Guid PatientId { get; private set; }

    /// <summary>The linked physical-file metadata; unique across all attachments.</summary>
    public Guid StoredFileId { get; private set; }

    /// <summary>Optional visit linkage; when present the visit must belong to the same patient.</summary>
    public Guid? VisitId { get; private set; }

    /// <summary>Server-generated event time (TimeProvider-sourced), normalized to UTC.</summary>
    public DateTimeOffset CreatedAtUtc { get; private set; }

    /// <summary>Authenticated staff actor of the upload; required (uploads are staff-only).</summary>
    public string CreatedByStaffId { get; private set; } = string.Empty;

    /// <summary>EF navigation to the linked file metadata (read projections only).</summary>
    public StoredFile File { get; private set; } = null!;

    private PatientAttachment() { } // EF Core

    public PatientAttachment(
        Guid patientId,
        Guid storedFileId,
        Guid? visitId,
        string createdByStaffId,
        DateTimeOffset createdAtUtc)
    {
        if (patientId == Guid.Empty)
            throw new ArgumentException("Patient ID is required.", nameof(patientId));
        if (storedFileId == Guid.Empty)
            throw new ArgumentException("Stored file ID is required.", nameof(storedFileId));
        if (visitId == Guid.Empty)
            throw new ArgumentException("Visit ID must be a real identifier or null.", nameof(visitId));
        var actor = createdByStaffId?.Trim();
        if (string.IsNullOrWhiteSpace(actor))
            throw new ArgumentException("Creator staff ID is required.", nameof(createdByStaffId));
        if (actor.Length > MaxCreatedByStaffIdLength)
            throw new ArgumentException(
                $"Creator staff ID must not exceed {MaxCreatedByStaffIdLength} characters.", nameof(createdByStaffId));

        Id = Guid.NewGuid();
        PatientId = patientId;
        StoredFileId = storedFileId;
        VisitId = visitId;
        CreatedByStaffId = actor;
        CreatedAtUtc = createdAtUtc.ToUniversalTime();
    }
}
