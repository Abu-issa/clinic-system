using Clinic.Domain.Enums;

namespace Clinic.Domain.Entities;

public sealed class PatientFamilyHistoryEntry : PatientClinicalEntry
{
    public string Relation { get; private set; } = string.Empty;

    public string Condition { get; private set; } = string.Empty;

    private PatientFamilyHistoryEntry()
    {
    }

    internal PatientFamilyHistoryEntry(
        Guid profileId,
        string relation,
        string condition,
        MedicalRecordSource source,
        string? recordedByStaffId,
        DateTimeOffset recordedAtUtc)
        : base(profileId, source, recordedByStaffId, recordedAtUtc)
    {
        Relation = RequiredText(relation, "Family history relation", 100);
        Condition = RequiredText(condition, "Family history condition", 200);
    }

    public bool SameClinicalContent(
        string relation,
        string condition,
        MedicalRecordSource source) =>
        Relation == RequiredText(relation, "Family history relation", 100) &&
        Condition == RequiredText(condition, "Family history condition", 200) &&
        Source == source;
}
