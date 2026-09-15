using Clinic.Domain.Enums;

namespace Clinic.Domain.Entities;

// Shared provenance and supersession columns for structured clinical entries.
// Clinical values are never destructively edited: changed entries are superseded
// (retained for history) and re-recorded as new rows.
public abstract class PatientClinicalEntry
{
    public Guid Id { get; private set; }

    public Guid ProfileId { get; private set; }

    public MedicalRecordSource Source { get; private set; }

    public ClinicalReviewStatus ReviewStatus { get; private set; }

    public DateTimeOffset RecordedAtUtc { get; private set; }

    // Null when the entry will be recorded directly by the patient (future self-service path).
    public string? RecordedByStaffId { get; private set; }

    public DateTimeOffset? VerifiedAtUtc { get; private set; }

    public string? VerifiedByStaffId { get; private set; }

    public DateTimeOffset? SupersededAtUtc { get; private set; }

    public string? SupersededByStaffId { get; private set; }

    protected PatientClinicalEntry()
    {
    }

    protected PatientClinicalEntry(
        Guid profileId,
        MedicalRecordSource source,
        string? recordedByStaffId,
        DateTimeOffset recordedAtUtc)
    {
        if (!Enum.IsDefined(source))
        {
            throw new ArgumentOutOfRangeException(nameof(source));
        }

        if (source == MedicalRecordSource.Staff &&
            string.IsNullOrWhiteSpace(recordedByStaffId))
        {
            throw new ArgumentException(
                "A staff identifier is required for staff-sourced entries.",
                nameof(recordedByStaffId));
        }

        ProfileId = profileId;
        Source = source;
        ReviewStatus = source == MedicalRecordSource.Patient
            ? ClinicalReviewStatus.PendingReview
            : ClinicalReviewStatus.Verified;
        if (ReviewStatus == ClinicalReviewStatus.Verified)
        {
            VerifiedAtUtc = recordedAtUtc.ToUniversalTime();
            VerifiedByStaffId = Normalize(recordedByStaffId);
        }
        RecordedByStaffId = Normalize(recordedByStaffId);
        RecordedAtUtc = recordedAtUtc.ToUniversalTime();
        Id = Guid.NewGuid();
    }

    public bool IsActive => SupersededAtUtc is null;

    internal void Verify(string staffId, DateTimeOffset verifiedAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(staffId);

        if (!IsActive)
        {
            throw new InvalidOperationException(
                "Superseded entries cannot be verified.");
        }

        if (ReviewStatus == ClinicalReviewStatus.Verified)
        {
            throw new InvalidOperationException(
                "The entry is already verified.");
        }

        ReviewStatus = ClinicalReviewStatus.Verified;
        VerifiedAtUtc = verifiedAtUtc.ToUniversalTime();
        VerifiedByStaffId = staffId.Trim();
    }

    internal void Supersede(string staffId, DateTimeOffset supersededAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(staffId);

        if (!IsActive)
        {
            throw new InvalidOperationException(
                "The entry is already superseded.");
        }

        SupersededAtUtc = supersededAtUtc.ToUniversalTime();
        SupersededByStaffId = staffId.Trim();
    }

    protected static string RequiredText(string value, string fieldName, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException(
                $"{fieldName} is required.",
                nameof(value));
        }

        var trimmed = value.Trim();

        if (trimmed.Length > maxLength)
        {
            throw new ArgumentException(
                $"{fieldName} cannot exceed {maxLength} characters.",
                nameof(value));
        }

        return trimmed;
    }

    protected static string? OptionalText(string? value, string fieldName, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();

        if (trimmed.Length > maxLength)
        {
            throw new ArgumentException(
                $"{fieldName} cannot exceed {maxLength} characters.",
                nameof(value));
        }

        return trimmed;
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
