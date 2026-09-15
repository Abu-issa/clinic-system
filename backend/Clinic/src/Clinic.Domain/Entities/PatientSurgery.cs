using Clinic.Domain.Enums;

namespace Clinic.Domain.Entities;

public sealed class PatientSurgery : PatientClinicalEntry
{
    public string ProcedureName { get; private set; } = string.Empty;

    // Optional because legacy histories often only state the procedure.
    public DateOnly? PerformedOn { get; private set; }

    private PatientSurgery()
    {
    }

    internal PatientSurgery(
        Guid profileId,
        string procedureName,
        DateOnly? performedOn,
        MedicalRecordSource source,
        string? recordedByStaffId,
        DateTimeOffset recordedAtUtc)
        : base(profileId, source, recordedByStaffId, recordedAtUtc)
    {
        ProcedureName = RequiredText(procedureName, "Surgery procedure name", 200);

        if (performedOn.HasValue && performedOn.Value > DateOnly.FromDateTime(recordedAtUtc.UtcDateTime))
        {
            throw new ArgumentException(
                "A surgery cannot be performed in the future.",
                nameof(performedOn));
        }

        PerformedOn = performedOn;
    }

    public bool SameClinicalContent(
        string procedureName,
        DateOnly? performedOn,
        MedicalRecordSource source) =>
        ProcedureName == RequiredText(procedureName, "Surgery procedure name", 200) &&
        PerformedOn == performedOn &&
        Source == source;
}
