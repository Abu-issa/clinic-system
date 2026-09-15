using Clinic.Domain.Enums;

namespace Clinic.Domain.Entities;

// Aggregate root for the structured medical profile. Clinical entries hang off
// the profile so their lifecycle (record, verify, supersede) is guarded here
// and never bypassed by direct collection mutation from callers.
public sealed class PatientMedicalProfile
{
    public Guid Id { get; private set; }

    public Guid PatientId { get; private set; }

    public BloodType? BloodType { get; private set; }

    public AllergyStatus AllergyStatus { get; private set; } = AllergyStatus.Unknown;

    public SmokingStatus? SmokingStatus { get; private set; }

    public DiabetesType? DiabetesType { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public string UpdatedByStaffId { get; private set; } = string.Empty;

    public byte[] RowVersion { get; private set; } = Array.Empty<byte>();

    public ICollection<PatientAllergy> Allergies { get; } = new List<PatientAllergy>();

    public ICollection<PatientChronicCondition> ChronicConditions { get; } = new List<PatientChronicCondition>();

    public ICollection<PatientMedication> Medications { get; } = new List<PatientMedication>();

    public ICollection<PatientSurgery> Surgeries { get; } = new List<PatientSurgery>();

    public ICollection<PatientFamilyHistoryEntry> FamilyHistory { get; } = new List<PatientFamilyHistoryEntry>();

    private PatientMedicalProfile()
    {
    }

    public PatientMedicalProfile(
        Guid patientId,
        string createdByStaffId,
        DateTimeOffset now)
    {
        if (patientId == Guid.Empty)
        {
            throw new ArgumentException(
                "Patient ID is required.",
                nameof(patientId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(createdByStaffId);

        Id = Guid.NewGuid();
        PatientId = patientId;
        CreatedAtUtc = now.ToUniversalTime();
        UpdatedAtUtc = now.ToUniversalTime();
        UpdatedByStaffId = createdByStaffId.Trim();
    }

    public void UpdateBasics(
        BloodType? bloodType,
        AllergyStatus allergyStatus,
        SmokingStatus? smokingStatus,
        DiabetesType? diabetesType,
        string staffId,
        DateTimeOffset now)
    {
        if (!Enum.IsDefined(allergyStatus))
        {
            throw new ArgumentOutOfRangeException(nameof(allergyStatus));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(staffId);

        if (allergyStatus == AllergyStatus.NoKnownAllergies &&
            Allergies.Any(allergy => allergy.IsActive))
        {
            throw new InvalidOperationException(
                "Allergy status cannot be NoKnownAllergies while active allergies are recorded.");
        }

        BloodType = bloodType;
        AllergyStatus = allergyStatus;
        SmokingStatus = smokingStatus;
        DiabetesType = diabetesType;
        UpdatedByStaffId = staffId.Trim();
        UpdatedAtUtc = now.ToUniversalTime();
    }

    public PatientAllergy RecordAllergy(
        string substance,
        string? reaction,
        AllergySeverity? severity,
        MedicalRecordSource source,
        string? staffId,
        DateTimeOffset now)
    {
        var entry = new PatientAllergy(Id, substance, reaction, severity, source, staffId, now);
        Allergies.Add(entry);
        Touch(staffId, now);
        return entry;
    }

    public PatientChronicCondition RecordChronicCondition(
        string conditionName,
        string? notes,
        MedicalRecordSource source,
        string? staffId,
        DateTimeOffset now)
    {
        var entry = new PatientChronicCondition(Id, conditionName, notes, source, staffId, now);
        ChronicConditions.Add(entry);
        Touch(staffId, now);
        return entry;
    }

    public PatientMedication RecordMedication(
        string medicationName,
        MedicationStatus status,
        MedicalRecordSource source,
        string? staffId,
        DateTimeOffset now)
    {
        var entry = new PatientMedication(Id, medicationName, status, source, staffId, now);
        Medications.Add(entry);
        Touch(staffId, now);
        return entry;
    }

    public PatientSurgery RecordSurgery(
        string procedureName,
        DateOnly? performedOn,
        MedicalRecordSource source,
        string? staffId,
        DateTimeOffset now)
    {
        var entry = new PatientSurgery(Id, procedureName, performedOn, source, staffId, now);
        Surgeries.Add(entry);
        Touch(staffId, now);
        return entry;
    }

    public PatientFamilyHistoryEntry RecordFamilyHistory(
        string relation,
        string condition,
        MedicalRecordSource source,
        string? staffId,
        DateTimeOffset now)
    {
        var entry = new PatientFamilyHistoryEntry(Id, relation, condition, source, staffId, now);
        FamilyHistory.Add(entry);
        Touch(staffId, now);
        return entry;
    }

    internal void VerifyEntry(PatientClinicalEntry entry, string staffId, DateTimeOffset now)
    {
        EnsureEntryBelongs(entry);
        entry.Verify(staffId, now);
        Touch(staffId, now);
    }

    internal void SupersedeEntry(PatientClinicalEntry entry, string staffId, DateTimeOffset now)
    {
        EnsureEntryBelongs(entry);
        entry.Supersede(staffId, now);
        Touch(staffId, now);
    }

    private void EnsureEntryBelongs(PatientClinicalEntry entry)
    {
        var belongs = entry switch
        {
            PatientAllergy allergy => Allergies.Contains(allergy),
            PatientChronicCondition condition => ChronicConditions.Contains(condition),
            PatientMedication medication => Medications.Contains(medication),
            PatientSurgery surgery => Surgeries.Contains(surgery),
            PatientFamilyHistoryEntry family => FamilyHistory.Contains(family),
            _ => false
        };

        if (!belongs || entry.ProfileId != Id)
        {
            throw new InvalidOperationException(
                "The entry does not belong to this medical profile.");
        }
    }

    private void Touch(string? staffId, DateTimeOffset now)
    {
        if (!string.IsNullOrWhiteSpace(staffId))
        {
            UpdatedByStaffId = staffId.Trim();
        }

        UpdatedAtUtc = now.ToUniversalTime();
    }
}
