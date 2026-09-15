using Clinic.Domain.Enums;

namespace Clinic.Api.Medications;

public sealed record MedicationUpsertBody(
    string? GenericNameEn,
    string? GenericNameAr,
    string? BrandNameEn,
    string? BrandNameAr,
    string? Strength,
    string? Unit,
    DosageForm Form,
    MedicationRoute Route,
    string? Category,
    byte[]? ExpectedRowVersion);

public sealed record MedicationVersionBody(byte[]? ExpectedRowVersion);

public sealed record MedicationResponse(
    Guid Id,
    string? GenericNameEn,
    string? GenericNameAr,
    string? BrandNameEn,
    string? BrandNameAr,
    string Strength,
    string Unit,
    DosageForm Form,
    MedicationRoute Route,
    string? Category,
    bool IsActive,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset LastModifiedAtUtc,
    byte[] RowVersion);

public sealed record MedicationListItemResponse(
    Guid Id,
    string? GenericNameEn,
    string? GenericNameAr,
    string? BrandNameEn,
    string? BrandNameAr,
    string Strength,
    string Unit,
    DosageForm Form,
    MedicationRoute Route,
    string? Category,
    bool IsActive);
