using Clinic.Domain.Entities;
using Clinic.Domain.Enums;

namespace Clinic.Application.Medications;

public enum MedicationCatalogError
{
    None = 0,
    InvalidInput = 1,
    MedicationNotFound = 2,
    InvalidRowVersion = 3,
    MedicationChanged = 4,
    DuplicateMedication = 5
}

public sealed record MedicationCatalogResult(bool IsSuccess, MedicationCatalogError Error, MedicationDetails? Details = null)
{
    public static MedicationCatalogResult Success(MedicationDetails details) => new(true, MedicationCatalogError.None, details);
    public static MedicationCatalogResult Failure(MedicationCatalogError error) => new(false, error);
}

public sealed record CreateMedicationRequest(
    string? GenericNameEn,
    string? GenericNameAr,
    string? BrandNameEn,
    string? BrandNameAr,
    string Strength,
    string Unit,
    DosageForm Form,
    MedicationRoute Route,
    string? Category);

public sealed record UpdateMedicationRequest(
    string? GenericNameEn,
    string? GenericNameAr,
    string? BrandNameEn,
    string? BrandNameAr,
    string Strength,
    string Unit,
    DosageForm Form,
    MedicationRoute Route,
    string? Category,
    byte[] ExpectedRowVersion);

public sealed record MedicationDetails(
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
    string CreatedByStaffId,
    DateTimeOffset LastModifiedAtUtc,
    string LastModifiedByStaffId,
    byte[] RowVersion);

public sealed record MedicationListItem(
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

public sealed record MedicationSearchQuery(
    string? SearchTerm = null,
    bool? ActiveOnly = true,
    int Skip = 0,
    int Take = 50);

public sealed record MedicationSearchResult(
    bool IsSuccess,
    MedicationCatalogError Error,
    IReadOnlyList<MedicationListItem> Items,
    int TotalCount);
