namespace Clinic.Domain.Enums;

// Structured medical-record enumerations. Nullable usages mean "not recorded";
// AllergyStatus is non-nullable because "Unknown" is itself a clinical statement
// that must survive staged migrations without inventing values.
public enum BloodType
{
    APositive = 1,
    ANegative = 2,
    BPositive = 3,
    BNegative = 4,
    AbPositive = 5,
    AbNegative = 6,
    OPositive = 7,
    ONegative = 8
}

public enum AllergyStatus
{
    Unknown = 0,
    NoKnownAllergies = 1,
    HasKnownAllergies = 2
}

public enum AllergySeverity
{
    Mild = 1,
    Moderate = 2,
    Severe = 3
}

public enum SmokingStatus
{
    Never = 1,
    Former = 2,
    Current = 3
}

public enum DiabetesType
{
    Type1 = 1,
    Type2 = 2,
    Gestational = 3,
    Other = 4
}

public enum MedicationStatus
{
    Current = 1,
    Past = 2
}

// Distinguishes who originally provided the clinical information. Patient-entered
// data starts pending review and must never silently replace verified data.
public enum MedicalRecordSource
{
    Staff = 1,
    Patient = 2
}

public enum ClinicalReviewStatus
{
    PendingReview = 1,
    Verified = 2
}
