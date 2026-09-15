using Clinic.Domain.Enums;

namespace Clinic.Api.Prescriptions;

public sealed record CreatePrescriptionBody(string? Notes);

public sealed record ReplacementBody(string? Notes, byte[]? ExpectedOriginalRowVersion);

public sealed record PrescriptionNotesBody(string? Notes, byte[]? ExpectedRowVersion);

public sealed record PrescriptionItemBody(
    Guid? MedicationId,
    string? Dose,
    string? Frequency,
    string? Duration,
    string? Instructions,
    int? DisplayOrder,
    byte[]? ExpectedRowVersion);

public sealed record PrescriptionItemReplaceBody(
    Guid? ReselectedMedicationId,
    string? Dose,
    string? Frequency,
    string? Duration,
    string? Instructions,
    int? DisplayOrder,
    byte[]? ExpectedRowVersion);

public sealed record PrescriptionItemRemoveBody(byte[]? ExpectedRowVersion);

public sealed record PrescriptionItemOrderBody(IReadOnlyList<Guid>? OrderedItemIds, byte[]? ExpectedRowVersion);

public sealed record PrescriptionVersionBody(byte[]? ExpectedRowVersion);

public sealed record PrescriptionCancelBody(string? Reason, byte[]? ExpectedRowVersion);

public sealed record PrescriptionItemResponse(
    Guid Id,
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
    DateTimeOffset CreatedAtUtc);

public sealed record PrescriptionResponse(
    Guid Id,
    Guid VisitId,
    Guid PatientId,
    Guid DoctorId,
    PrescriptionStatus Status,
    Guid? ReplacesPrescriptionId,
    Guid? ReplacedByPrescriptionId,
    string? Notes,
    DateTimeOffset? FinalizedAtUtc,
    DateTimeOffset? ReleasedAtUtc,
    string? CancellationReason,
    DateTimeOffset? CancelledAtUtc,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset LastModifiedAtUtc,
    byte[] RowVersion,
    IReadOnlyList<PrescriptionItemResponse> Items);

public sealed record PrescriptionListItemResponse(
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
