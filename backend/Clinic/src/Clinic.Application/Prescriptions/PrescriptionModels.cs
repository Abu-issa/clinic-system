using Clinic.Domain.Entities;
using Clinic.Domain.Enums;

namespace Clinic.Application.Prescriptions;

public enum PrescriptionError
{
    None = 0,
    InvalidInput = 1,
    VisitNotFound = 2,
    PrescriptionNotFound = 3,
    MedicationNotFound = 4,
    InactiveMedication = 5,
    PrescriptionItemNotFound = 6,
    InvalidRowVersion = 7,
    PrescriptionChanged = 8,
    InvalidLifecycle = 9,
    ReplacementMismatch = 10,
    NotPrintable = 11
}

public sealed record PrescriptionResult(bool IsSuccess, PrescriptionError Error, PrescriptionDetails? Details = null)
{
    public static PrescriptionResult Success(PrescriptionDetails details) => new(true, PrescriptionError.None, details);
    public static PrescriptionResult Failure(PrescriptionError error) => new(false, error);
}

/// <summary>
/// Creates a draft prescription. When ReplacesPrescriptionId targets a cancelled prescription,
/// ExpectedOriginalRowVersion must carry that prescription's current 8-byte rowversion: creating
/// the replacement also marks the cancelled original as replaced, and a stale version loses that race.
/// </summary>
public sealed record CreatePrescriptionDraftRequest(
    Guid VisitId,
    string? Notes = null,
    Guid? ReplacesPrescriptionId = null,
    byte[]? ExpectedOriginalRowVersion = null);

public sealed record UpdatePrescriptionNotesRequest(
    string? Notes,
    byte[] ExpectedRowVersion);

public sealed record AddPrescriptionItemRequest(
    Guid MedicationId,
    string? Dose,
    string? Frequency,
    string? Duration,
    string? Instructions,
    int? DisplayOrder,
    byte[] ExpectedRowVersion);

public sealed record UpdatePrescriptionItemRequest(
    Guid? ReselectedMedicationId,
    string? Dose,
    string? Frequency,
    string? Duration,
    string? Instructions,
    int? DisplayOrder,
    byte[] ExpectedRowVersion);

public sealed record ReorderPrescriptionItemsRequest(
    IReadOnlyList<Guid> OrderedItemIds,
    byte[] ExpectedRowVersion);

public sealed record CancelPrescriptionRequest(
    string Reason,
    byte[] ExpectedRowVersion);

public sealed record PrescriptionItemDetails(
    Guid Id,
    Guid PrescriptionId,
    Guid MedicationId,
    string? GenericNameEn,
    string? GenericNameAr,
    string? BrandNameEn,
    string? BrandNameAr,
    string Strength,
    string Unit,
    DosageForm Form,
    MedicationRoute Route,
    string? Dose,
    string? Frequency,
    string? Duration,
    string? Instructions,
    int DisplayOrder,
    DateTimeOffset CreatedAtUtc,
    string CreatedByStaffId);

public sealed record PrescriptionDetails(
    Guid Id,
    Guid VisitId,
    Guid PatientId,
    Guid DoctorId,
    PrescriptionStatus Status,
    Guid? ReplacesPrescriptionId,
    Guid? ReplacedByPrescriptionId,
    string? Notes,
    DateTimeOffset? FinalizedAtUtc,
    string? FinalizedByStaffId,
    DateTimeOffset? ReleasedAtUtc,
    string? ReleasedByStaffId,
    string? CancellationReason,
    DateTimeOffset? CancelledAtUtc,
    string? CancelledByStaffId,
    DateTimeOffset CreatedAtUtc,
    string CreatedByStaffId,
    DateTimeOffset LastModifiedAtUtc,
    string LastModifiedByStaffId,
    byte[] RowVersion,
    IReadOnlyList<PrescriptionItemDetails> Items);

public sealed record PrescriptionListItem(
    Guid Id,
    Guid VisitId,
    Guid PatientId,
    Guid DoctorId,
    PrescriptionStatus Status,
    int ItemCount,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? FinalizedAtUtc,
    DateTimeOffset? ReleasedAtUtc,
    DateTimeOffset? CancelledAtUtc);

public sealed record PrescriptionListResult(
    bool IsSuccess,
    PrescriptionError Error,
    IReadOnlyList<PrescriptionListItem> Prescriptions);

public sealed record VisitPrescriptionReference(
    Guid PatientId,
    Guid DoctorId,
    VisitStatus Status);
