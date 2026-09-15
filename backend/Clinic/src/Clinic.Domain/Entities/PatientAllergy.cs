using Clinic.Domain.Enums;

namespace Clinic.Domain.Entities;

public sealed class PatientAllergy : PatientClinicalEntry
{
    public string Substance { get; private set; } = string.Empty;

    public string? Reaction { get; private set; }

    public AllergySeverity? Severity { get; private set; }

    private PatientAllergy()
    {
    }

    internal PatientAllergy(
        Guid profileId,
        string substance,
        string? reaction,
        AllergySeverity? severity,
        MedicalRecordSource source,
        string? recordedByStaffId,
        DateTimeOffset recordedAtUtc)
        : base(profileId, source, recordedByStaffId, recordedAtUtc)
    {
        Substance = RequiredText(substance, "Allergy substance", 200);
        Reaction = OptionalText(reaction, "Allergy reaction", 500);

        if (severity.HasValue && !Enum.IsDefined(severity.Value))
        {
            throw new ArgumentOutOfRangeException(nameof(severity));
        }

        Severity = severity;
    }

    public bool SameClinicalContent(
        string substance,
        string? reaction,
        AllergySeverity? severity,
        MedicalRecordSource source) =>
        Substance == RequiredText(substance, "Allergy substance", 200) &&
        Reaction == OptionalText(reaction, "Allergy reaction", 500) &&
        Severity == severity &&
        Source == source;
}
