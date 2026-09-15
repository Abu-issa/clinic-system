using Clinic.Domain.Enums;

namespace Clinic.Application.Patients;

public enum PatientAdminError
{
    None,
    InvalidMedicalRecordNumber,
    InvalidPatientName,
    InvalidPhoneNumber,
    InvalidDateOfBirth,
    InvalidEmergencyContact,
    InvalidLegacyReferences,
    MedicalRecordNumberAlreadyExists,
    PatientNotFound,
    InvalidRowVersion,
    PatientChanged
}

public enum MedicalProfileError
{
    None,
    PatientNotFound,
    InvalidActor,
    InvalidRowVersion,
    ProfileChanged,
    InvalidAllergyStatus,
    InvalidEntryReference,
    EntryCannotBeUnverified,
    InconsistentAllergyStatus
}

public sealed record CreatePatientRequest(
    string FullName,
    string PhoneNumber,
    DateOnly? DateOfBirth,
    string MedicalRecordNumber,
    string? LegacyPaperFileNumber,
    string? LegacyCoverImageReference,
    string? EmergencyContactName,
    string? EmergencyContactPhone,
    string? EmergencyContactRelation);

public sealed record UpdatePatientRequest(
    string FullName,
    string PhoneNumber,
    DateOnly? DateOfBirth,
    string? EmergencyContactName,
    string? EmergencyContactPhone,
    string? EmergencyContactRelation,
    byte[] ExpectedRowVersion);

public sealed record PatientAdminRecordDetails(
    Guid PatientId,
    string? MedicalRecordNumber,
    string? LegacyPaperFileNumber,
    string? LegacyCoverImageReference,
    string FullName,
    string PhoneNumber,
    DateOnly? DateOfBirth,
    string? EmergencyContactName,
    string? EmergencyContactPhone,
    string? EmergencyContactRelation,
    DateTimeOffset CreatedAtUtc,
    byte[] RowVersion);

public sealed record PatientAdminResult(
    bool IsSuccess,
    PatientAdminError Error,
    PatientAdminRecordDetails? Details = null)
{
    public static PatientAdminResult Success(PatientAdminRecordDetails details) => new(true, PatientAdminError.None, details);
    public static PatientAdminResult Failure(PatientAdminError error) => new(false, error);
}

// Clinical entry inputs. Id is set when replacing an existing active entry;
// omitted ids create new entries and missing existing entries are superseded.
public sealed record AllergyInput(
    Guid? Id,
    string Substance,
    string? Reaction,
    AllergySeverity? Severity,
    MedicalRecordSource Source,
    ClinicalReviewStatus ReviewStatus);

public sealed record ChronicConditionInput(
    Guid? Id,
    string ConditionName,
    string? Notes,
    MedicalRecordSource Source,
    ClinicalReviewStatus ReviewStatus);

public sealed record MedicationInput(
    Guid? Id,
    string MedicationName,
    MedicationStatus Status,
    MedicalRecordSource Source,
    ClinicalReviewStatus ReviewStatus);

public sealed record SurgeryInput(
    Guid? Id,
    string ProcedureName,
    DateOnly? PerformedOn,
    MedicalRecordSource Source,
    ClinicalReviewStatus ReviewStatus);

public sealed record FamilyHistoryInput(
    Guid? Id,
    string Relation,
    string Condition,
    MedicalRecordSource Source,
    ClinicalReviewStatus ReviewStatus);

public sealed record SaveMedicalProfileRequest(
    BloodType? BloodType,
    AllergyStatus AllergyStatus,
    SmokingStatus? SmokingStatus,
    DiabetesType? DiabetesType,
    IReadOnlyList<AllergyInput> Allergies,
    IReadOnlyList<ChronicConditionInput> ChronicConditions,
    IReadOnlyList<MedicationInput> Medications,
    IReadOnlyList<SurgeryInput> Surgeries,
    IReadOnlyList<FamilyHistoryInput> FamilyHistory,
    byte[]? ExpectedRowVersion);

// Response projections deliberately exclude staff identifiers: attribution is
// persisted, but internal account IDs are not exposed through the API.
public sealed record AllergyDetails(
    Guid Id,
    string Substance,
    string? Reaction,
    AllergySeverity? Severity,
    MedicalRecordSource Source,
    ClinicalReviewStatus ReviewStatus,
    DateTimeOffset RecordedAtUtc,
    DateTimeOffset? VerifiedAtUtc);

public sealed record ChronicConditionDetails(
    Guid Id,
    string ConditionName,
    string? Notes,
    MedicalRecordSource Source,
    ClinicalReviewStatus ReviewStatus,
    DateTimeOffset RecordedAtUtc,
    DateTimeOffset? VerifiedAtUtc);

public sealed record MedicationDetails(
    Guid Id,
    string MedicationName,
    MedicationStatus Status,
    MedicalRecordSource Source,
    ClinicalReviewStatus ReviewStatus,
    DateTimeOffset RecordedAtUtc,
    DateTimeOffset? VerifiedAtUtc);

public sealed record SurgeryDetails(
    Guid Id,
    string ProcedureName,
    DateOnly? PerformedOn,
    MedicalRecordSource Source,
    ClinicalReviewStatus ReviewStatus,
    DateTimeOffset RecordedAtUtc,
    DateTimeOffset? VerifiedAtUtc);

public sealed record FamilyHistoryDetails(
    Guid Id,
    string Relation,
    string Condition,
    MedicalRecordSource Source,
    ClinicalReviewStatus ReviewStatus,
    DateTimeOffset RecordedAtUtc,
    DateTimeOffset? VerifiedAtUtc);

public sealed record MedicalProfileDetails(
    Guid PatientId,
    BloodType? BloodType,
    AllergyStatus AllergyStatus,
    SmokingStatus? SmokingStatus,
    DiabetesType? DiabetesType,
    DateTimeOffset UpdatedAtUtc,
    byte[] RowVersion,
    IReadOnlyList<AllergyDetails> Allergies,
    IReadOnlyList<ChronicConditionDetails> ChronicConditions,
    IReadOnlyList<MedicationDetails> Medications,
    IReadOnlyList<SurgeryDetails> Surgeries,
    IReadOnlyList<FamilyHistoryDetails> FamilyHistory)
{
    public static MedicalProfileDetails Empty(Guid patientId) => new(
        patientId, null, AllergyStatus.Unknown, null, null,
        DateTimeOffset.MinValue, Array.Empty<byte>(),
        Array.Empty<AllergyDetails>(), Array.Empty<ChronicConditionDetails>(),
        Array.Empty<MedicationDetails>(), Array.Empty<SurgeryDetails>(),
        Array.Empty<FamilyHistoryDetails>());
}

public sealed record MedicalProfileResult(
    bool IsSuccess,
    MedicalProfileError Error,
    MedicalProfileDetails? Details = null)
{
    public static MedicalProfileResult Success(MedicalProfileDetails details) => new(true, MedicalProfileError.None, details);
    public static MedicalProfileResult Failure(MedicalProfileError error) => new(false, error);
}
