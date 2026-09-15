using Clinic.Domain.Enums;

namespace Clinic.Domain.Entities;

/// <summary>
/// Prescription item with catalog snapshot captured at creation time.
/// Child entity of Prescription aggregate.
/// </summary>
public sealed class PrescriptionItem
{
    public Guid Id { get; private set; }
    public Guid PrescriptionId { get; private set; }

    /// <summary>Reference to catalog medication. Snapshot fields preserve historical details.</summary>
    public Guid MedicationId { get; private set; }

    // Catalog snapshot fields (copied from Medication at insertion time)
    public string? GenericNameEn { get; private set; }
    public string? GenericNameAr { get; private set; }
    public string? BrandNameEn { get; private set; }
    public string? BrandNameAr { get; private set; }
    public string Strength { get; private set; } = string.Empty;
    public string Unit { get; private set; } = string.Empty;
    public DosageForm Form { get; private set; }
    public MedicationRoute Route { get; private set; }

    // Clinician prescription instructions (bounded text). Dose, frequency and duration may be
    // incomplete while the prescription is a Draft; structural completeness is enforced by the
    // aggregate at finalization (Prescription.FinalizePrescription). Values stay bounded at all times.
    /// <summary>Prescribed dose (e.g., "1 tablet", "5 ml"). Max 200 chars; null while the draft is incomplete.</summary>
    public string? Dose { get; private set; }

    /// <summary>Frequency (e.g., "twice daily", "every 6 hours"). Max 200 chars; null while the draft is incomplete.</summary>
    public string? Frequency { get; private set; }

    /// <summary>Duration (e.g., "7 days", "2 weeks"). Max 200 chars; null while the draft is incomplete.</summary>
    public string? Duration { get; private set; }

    /// <summary>Additional instructions (optional, max 1000 chars).</summary>
    public string? Instructions { get; private set; }

    /// <summary>Display order within prescription (0-based, stable).</summary>
    public int DisplayOrder { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }
    public string CreatedByStaffId { get; private set; } = string.Empty;

    private PrescriptionItem() { } // EF Core

    internal PrescriptionItem(
        Guid prescriptionId,
        Medication medication,
        string? dose,
        string? frequency,
        string? duration,
        string? instructions,
        int displayOrder,
        string createdByStaffId,
        DateTimeOffset createdAtUtc)
    {
        dose = NormalizeOptional(dose, nameof(dose), 200);
        frequency = NormalizeOptional(frequency, nameof(frequency), 200);
        duration = NormalizeOptional(duration, nameof(duration), 200);
        var normalizedInstructions = NormalizeOptional(instructions, nameof(instructions), 1000);
        if (displayOrder < 0)
            throw new ArgumentException("Display order must be non-negative.", nameof(displayOrder));
        if (string.IsNullOrWhiteSpace(createdByStaffId))
            throw new ArgumentException("Creator staff ID is required.", nameof(createdByStaffId));

        Id = Guid.NewGuid();
        PrescriptionId = prescriptionId;
        MedicationId = medication.Id;

        // Snapshot catalog details
        GenericNameEn = medication.GenericNameEn;
        GenericNameAr = medication.GenericNameAr;
        BrandNameEn = medication.BrandNameEn;
        BrandNameAr = medication.BrandNameAr;
        Strength = medication.Strength;
        Unit = medication.Unit;
        Form = medication.Form;
        Route = medication.Route;

        // Clinician instructions
        Dose = dose;
        Frequency = frequency;
        Duration = duration;
        Instructions = normalizedInstructions;
        DisplayOrder = displayOrder;

        CreatedByStaffId = createdByStaffId;
        CreatedAtUtc = createdAtUtc;
    }

    /// <summary>
    /// Update clinician instructions and optionally refresh catalog snapshot (Draft only).
    /// Dose, frequency and duration may remain incomplete while the prescription is a Draft.
    /// </summary>
    internal void Update(
        Medication? reselectedMedication,
        string? dose,
        string? frequency,
        string? duration,
        string? instructions,
        int displayOrder)
    {
        dose = NormalizeOptional(dose, nameof(dose), 200);
        frequency = NormalizeOptional(frequency, nameof(frequency), 200);
        duration = NormalizeOptional(duration, nameof(duration), 200);
        instructions = NormalizeOptional(instructions, nameof(instructions), 1000);
        if (displayOrder < 0)
            throw new ArgumentException("Display order must be non-negative.", nameof(displayOrder));

        // Refresh snapshot if medication was explicitly reselected
        if (reselectedMedication != null && reselectedMedication.Id == MedicationId)
        {
            GenericNameEn = reselectedMedication.GenericNameEn;
            GenericNameAr = reselectedMedication.GenericNameAr;
            BrandNameEn = reselectedMedication.BrandNameEn;
            BrandNameAr = reselectedMedication.BrandNameAr;
            Strength = reselectedMedication.Strength;
            Unit = reselectedMedication.Unit;
            Form = reselectedMedication.Form;
            Route = reselectedMedication.Route;
        }

        Dose = dose;
        Frequency = frequency;
        Duration = duration;
        Instructions = instructions;
        DisplayOrder = displayOrder;
    }

    /// <summary>
    /// Check if the item is structurally complete (has all required clinician instructions).
    /// </summary>
    public bool IsComplete() =>
        !string.IsNullOrWhiteSpace(Dose) &&
        !string.IsNullOrWhiteSpace(Frequency) &&
        !string.IsNullOrWhiteSpace(Duration);

    /// <summary>
    /// Draft fields may be absent but never oversized; whitespace-only input is stored as null.
    /// </summary>
    private static string? NormalizeOptional(string? value, string name, int maxLength)
    {
        if (value is null || string.IsNullOrWhiteSpace(value))
            return null;
        if (value.Length > maxLength)
            throw new ArgumentException($"{name} must not exceed {maxLength} characters.", name);
        return value.Trim();
    }
}
