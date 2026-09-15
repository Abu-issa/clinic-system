using Clinic.Domain.Enums;

namespace Clinic.Domain.Entities;

public sealed class PatientChronicCondition : PatientClinicalEntry
{
    public string ConditionName { get; private set; } = string.Empty;

    public string? Notes { get; private set; }

    private PatientChronicCondition()
    {
    }

    internal PatientChronicCondition(
        Guid profileId,
        string conditionName,
        string? notes,
        MedicalRecordSource source,
        string? recordedByStaffId,
        DateTimeOffset recordedAtUtc)
        : base(profileId, source, recordedByStaffId, recordedAtUtc)
    {
        ConditionName = RequiredText(conditionName, "Chronic condition name", 200);
        Notes = OptionalText(notes, "Chronic condition notes", 500);
    }

    public bool SameClinicalContent(
        string conditionName,
        string? notes,
        MedicalRecordSource source) =>
        ConditionName == RequiredText(conditionName, "Chronic condition name", 200) &&
        Notes == OptionalText(notes, "Chronic condition notes", 500) &&
        Source == source;
}
