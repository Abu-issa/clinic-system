namespace Clinic.Domain.Enums;

/// <summary>
/// Prescription lifecycle status.
/// </summary>
public enum PrescriptionStatus
{
    /// <summary>Draft: editable, may be incomplete.</summary>
    Draft = 0,

    /// <summary>Finalized: requires ≥1 item, items immutable, can be released or cancelled.</summary>
    Finalized = 1,

    /// <summary>Released: issued to patient (separate step from finalization).</summary>
    Released = 2,

    /// <summary>Cancelled: from Finalized/Released with required reason; cannot be released after cancellation.</summary>
    Cancelled = 3
}

/// <summary>
/// Medication dosage form.
/// </summary>
public enum DosageForm
{
    Tablet = 0,
    Capsule = 1,
    Syrup = 2,
    Suspension = 3,
    Injection = 4,
    Ointment = 5,
    Cream = 6,
    Drops = 7,
    Inhaler = 8,
    Suppository = 9,
    Patch = 10,
    Other = 99
}

/// <summary>
/// Medication administration route.
/// </summary>
public enum MedicationRoute
{
    Oral = 0,
    Sublingual = 1,
    Topical = 2,
    Intravenous = 3,
    Intramuscular = 4,
    Subcutaneous = 5,
    Inhalation = 6,
    Ophthalmic = 7,
    Otic = 8,
    Nasal = 9,
    Rectal = 10,
    Other = 99
}
