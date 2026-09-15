using Clinic.Domain.Enums;

namespace Clinic.Domain.Entities;

/// <summary>
/// Medication catalog entry with multi-lingual names and active/inactive status.
/// Aggregate root with optimistic concurrency via RowVersion.
/// </summary>
public sealed class Medication
{
    public Guid Id { get; private set; }

    /// <summary>English generic name. At least one generic name (En or Ar) is required.</summary>
    public string? GenericNameEn { get; private set; }

    /// <summary>Arabic generic name. At least one generic name (En or Ar) is required.</summary>
    public string? GenericNameAr { get; private set; }

    /// <summary>English brand name (optional).</summary>
    public string? BrandNameEn { get; private set; }

    /// <summary>Arabic brand name (optional).</summary>
    public string? BrandNameAr { get; private set; }

    /// <summary>
    /// Strength value without its unit: a compact token containing no whitespace, e.g. "500",
    /// "12.5". Compound strengths separate components with "/", e.g. "500/125" for
    /// amoxicillin 500 mg / clavulanate 125 mg per tablet; Unit then names the shared unit.
    /// Ratios over a volume are expressed with a ratio unit, e.g. Strength "5" + Unit "mg/ml".
    /// Never embed the unit in the strength ("500 mg" is rejected): the pair must stay unambiguous.
    /// </summary>
    public string Strength { get; private set; } = string.Empty;

    /// <summary>Unit token without any value, e.g. "mg", "ml", "mcg", "IU", "mg/ml". Max 50 chars, no whitespace.</summary>
    public string Unit { get; private set; } = string.Empty;

    /// <summary>Dosage form (Tablet, Capsule, Syrup, etc.).</summary>
    public DosageForm Form { get; private set; }

    /// <summary>Administration route (Oral, Topical, Intravenous, etc.).</summary>
    public MedicationRoute Route { get; private set; }

    /// <summary>Category (e.g., "Antibiotic", "Analgesic"). Optional, max 100 chars.</summary>
    public string? Category { get; private set; }

    /// <summary>Active status. Inactive medications cannot be newly selected in prescriptions.</summary>
    public bool IsActive { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }
    public string CreatedByStaffId { get; private set; } = string.Empty;
    public DateTimeOffset LastModifiedAtUtc { get; private set; }
    public string LastModifiedByStaffId { get; private set; } = string.Empty;

    /// <summary>SQL Server RowVersion for optimistic concurrency.</summary>
    public byte[] RowVersion { get; private set; } = [];

    private Medication() { } // EF Core

    public Medication(
        string? genericNameEn,
        string? genericNameAr,
        string? brandNameEn,
        string? brandNameAr,
        string strength,
        string unit,
        DosageForm form,
        MedicationRoute route,
        string? category,
        string createdByStaffId,
        DateTimeOffset createdAtUtc)
    {
        if (string.IsNullOrWhiteSpace(genericNameEn) && string.IsNullOrWhiteSpace(genericNameAr))
            throw new ArgumentException("At least one generic name (English or Arabic) is required.");
        ValidateStrengthAndUnit(strength, unit);
        if (!string.IsNullOrEmpty(genericNameEn) && genericNameEn.Length > 200)
            throw new ArgumentException("Generic name (English) must not exceed 200 characters.", nameof(genericNameEn));
        if (!string.IsNullOrEmpty(genericNameAr) && genericNameAr.Length > 200)
            throw new ArgumentException("Generic name (Arabic) must not exceed 200 characters.", nameof(genericNameAr));
        if (!string.IsNullOrEmpty(brandNameEn) && brandNameEn.Length > 200)
            throw new ArgumentException("Brand name (English) must not exceed 200 characters.", nameof(brandNameEn));
        if (!string.IsNullOrEmpty(brandNameAr) && brandNameAr.Length > 200)
            throw new ArgumentException("Brand name (Arabic) must not exceed 200 characters.", nameof(brandNameAr));
        if (!string.IsNullOrEmpty(category) && category.Length > 100)
            throw new ArgumentException("Category must not exceed 100 characters.", nameof(category));
        if (string.IsNullOrWhiteSpace(createdByStaffId))
            throw new ArgumentException("Creator staff ID is required.", nameof(createdByStaffId));

        Id = Guid.NewGuid();
        GenericNameEn = string.IsNullOrWhiteSpace(genericNameEn) ? null : genericNameEn.Trim();
        GenericNameAr = string.IsNullOrWhiteSpace(genericNameAr) ? null : genericNameAr.Trim();
        BrandNameEn = string.IsNullOrWhiteSpace(brandNameEn) ? null : brandNameEn.Trim();
        BrandNameAr = string.IsNullOrWhiteSpace(brandNameAr) ? null : brandNameAr.Trim();
        Strength = strength.Trim();
        Unit = unit.Trim();
        Form = form;
        Route = route;
        Category = string.IsNullOrWhiteSpace(category) ? null : category.Trim();
        IsActive = true; // New medications start active
        CreatedByStaffId = createdByStaffId;
        CreatedAtUtc = createdAtUtc;
        LastModifiedByStaffId = createdByStaffId;
        LastModifiedAtUtc = createdAtUtc;
    }

    public void UpdateDetails(
        string? genericNameEn,
        string? genericNameAr,
        string? brandNameEn,
        string? brandNameAr,
        string strength,
        string unit,
        DosageForm form,
        MedicationRoute route,
        string? category,
        string modifiedByStaffId,
        DateTimeOffset modifiedAtUtc)
    {
        if (string.IsNullOrWhiteSpace(genericNameEn) && string.IsNullOrWhiteSpace(genericNameAr))
            throw new ArgumentException("At least one generic name (English or Arabic) is required.");
        ValidateStrengthAndUnit(strength, unit);
        if (!string.IsNullOrEmpty(genericNameEn) && genericNameEn.Length > 200)
            throw new ArgumentException("Generic name (English) must not exceed 200 characters.", nameof(genericNameEn));
        if (!string.IsNullOrEmpty(genericNameAr) && genericNameAr.Length > 200)
            throw new ArgumentException("Generic name (Arabic) must not exceed 200 characters.", nameof(genericNameAr));
        if (!string.IsNullOrEmpty(brandNameEn) && brandNameEn.Length > 200)
            throw new ArgumentException("Brand name (English) must not exceed 200 characters.", nameof(brandNameEn));
        if (!string.IsNullOrEmpty(brandNameAr) && brandNameAr.Length > 200)
            throw new ArgumentException("Brand name (Arabic) must not exceed 200 characters.", nameof(brandNameAr));
        if (!string.IsNullOrEmpty(category) && category.Length > 100)
            throw new ArgumentException("Category must not exceed 100 characters.", nameof(category));
        if (string.IsNullOrWhiteSpace(modifiedByStaffId))
            throw new ArgumentException("Modifier staff ID is required.", nameof(modifiedByStaffId));

        GenericNameEn = string.IsNullOrWhiteSpace(genericNameEn) ? null : genericNameEn.Trim();
        GenericNameAr = string.IsNullOrWhiteSpace(genericNameAr) ? null : genericNameAr.Trim();
        BrandNameEn = string.IsNullOrWhiteSpace(brandNameEn) ? null : brandNameEn.Trim();
        BrandNameAr = string.IsNullOrWhiteSpace(brandNameAr) ? null : brandNameAr.Trim();
        Strength = strength.Trim();
        Unit = unit.Trim();
        Form = form;
        Route = route;
        Category = string.IsNullOrWhiteSpace(category) ? null : category.Trim();
        LastModifiedByStaffId = modifiedByStaffId;
        LastModifiedAtUtc = modifiedAtUtc;
    }

    public void Deactivate(string modifiedByStaffId, DateTimeOffset modifiedAtUtc)
    {
        if (string.IsNullOrWhiteSpace(modifiedByStaffId))
            throw new ArgumentException("Modifier staff ID is required.", nameof(modifiedByStaffId));
        if (!IsActive)
            return; // Idempotent

        IsActive = false;
        LastModifiedByStaffId = modifiedByStaffId;
        LastModifiedAtUtc = modifiedAtUtc;
    }

    public void Activate(string modifiedByStaffId, DateTimeOffset modifiedAtUtc)
    {
        if (string.IsNullOrWhiteSpace(modifiedByStaffId))
            throw new ArgumentException("Modifier staff ID is required.", nameof(modifiedByStaffId));
        if (IsActive)
            return; // Idempotent

        IsActive = true;
        LastModifiedByStaffId = modifiedByStaffId;
        LastModifiedAtUtc = modifiedAtUtc;
    }

    /// <summary>
    /// Strength and unit are stored as separate compact tokens. Embedded units ("500 mg") make the
    /// pair ambiguous and are rejected; compound strengths are "/"-separated values, not sentences.
    /// </summary>
    private static void ValidateStrengthAndUnit(string strength, string unit)
    {
        if (string.IsNullOrWhiteSpace(strength))
            throw new ArgumentException("Strength is required.", nameof(strength));
        if (strength.Length > 100)
            throw new ArgumentException("Strength must not exceed 100 characters.", nameof(strength));
        if (strength.Any(char.IsWhiteSpace))
            throw new ArgumentException(
                "Strength must contain only the value without its unit (e.g. \"500\", compound \"500/125\"); \"500 mg\" style input is rejected.",
                nameof(strength));
        if (string.IsNullOrWhiteSpace(unit))
            throw new ArgumentException("Unit is required.", nameof(unit));
        if (unit.Length > 50)
            throw new ArgumentException("Unit must not exceed 50 characters.", nameof(unit));
        if (unit.Any(char.IsWhiteSpace))
            throw new ArgumentException(
                "Unit must contain only the unit token (e.g. \"mg\", \"mg/ml\"), without the value.",
                nameof(unit));
    }
}
