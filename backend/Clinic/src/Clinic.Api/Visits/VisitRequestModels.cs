using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Clinic.Api.Visits;

public sealed record CreateVisitBody(
    [Required] Guid? DoctorId,
    Guid? AppointmentId,
    [Required] DateTimeOffset? OccurredAtUtc);

public sealed record UpdateVisitBody(
    string? ChiefComplaint,
    string? Symptoms,
    string? Diagnosis,
    string? ClinicianNotes,
    string? InternalNotes,
    string? PatientSummary,
    DateTimeOffset? SuggestedFollowUpAtUtc,
    [Required, MinLength(1)] byte[]? ExpectedRowVersion);

public sealed record FinalizeVisitBody(
    [Required, MinLength(1)] byte[]? ExpectedRowVersion);

public sealed record AddAmendmentBody(
    [Required, StringLength(8000, MinimumLength = 1)] string? Reason,
    [Required, StringLength(8000, MinimumLength = 1)] string? AmendmentText,
    [Required, MinLength(1)] byte[]? ExpectedRowVersion);

[JsonDerivedType(typeof(VitalReadingBody.BloodPressureBody), "BloodPressure")]
[JsonDerivedType(typeof(VitalReadingBody.HeartRateBody), "HeartRate")]
[JsonDerivedType(typeof(VitalReadingBody.TemperatureBody), "Temperature")]
[JsonDerivedType(typeof(VitalReadingBody.OxygenSaturationBody), "OxygenSaturation")]
[JsonDerivedType(typeof(VitalReadingBody.WeightBody), "Weight")]
[JsonDerivedType(typeof(VitalReadingBody.HeightBody), "Height")]
[JsonDerivedType(typeof(VitalReadingBody.RespiratoryRateBody), "RespiratoryRate")]
public abstract record VitalReadingBody
{
    private VitalReadingBody() { }

    public sealed record BloodPressureBody([Required] int? Systolic, [Required] int? Diastolic) : VitalReadingBody;
    public sealed record HeartRateBody([Required] int? Bpm) : VitalReadingBody;
    public sealed record TemperatureBody([Required] decimal? Celsius) : VitalReadingBody;
    public sealed record OxygenSaturationBody([Required] decimal? Percent) : VitalReadingBody;
    public sealed record WeightBody([Required] decimal? Kg) : VitalReadingBody;
    public sealed record HeightBody([Required] decimal? Cm) : VitalReadingBody;
    public sealed record RespiratoryRateBody([Required] int? BreathsPerMinute) : VitalReadingBody;
}

public sealed record AddVitalBody(
    [Required] VitalReadingBody? Reading,
    [Required] DateTimeOffset? MeasuredAtUtc,
    [Required, MinLength(1)] byte[]? ExpectedRowVersion);
