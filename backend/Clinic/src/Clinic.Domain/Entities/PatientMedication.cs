using Clinic.Domain.Enums;

namespace Clinic.Domain.Entities;

// Medication names only: doses, frequency and other clinical prescribing facts
// are deliberately out of scope for this foundation.
public sealed class PatientMedication : PatientClinicalEntry
{
    public string MedicationName { get; private set; } = string.Empty;

    public MedicationStatus Status { get; private set; }

    private PatientMedication()
    {
    }

    internal PatientMedication(
        Guid profileId,
        string medicationName,
        MedicationStatus status,
        MedicalRecordSource source,
        string? recordedByStaffId,
        DateTimeOffset recordedAtUtc)
        : base(profileId, source, recordedByStaffId, recordedAtUtc)
    {
        MedicationName = RequiredText(medicationName, "Medication name", 200);

        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        Status = status;
    }

    public bool SameClinicalContent(
        string medicationName,
        MedicationStatus status,
        MedicalRecordSource source) =>
        MedicationName == RequiredText(medicationName, "Medication name", 200) &&
        Status == status &&
        Source == source;
}
