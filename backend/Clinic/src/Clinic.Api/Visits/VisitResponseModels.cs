using Clinic.Domain.Enums;

namespace Clinic.Api.Visits;

public sealed record VisitResponse(
    Guid Id,
    Guid PatientId,
    Guid DoctorId,
    Guid? AppointmentId,
    DateTimeOffset OccurredAtUtc,
    VisitStatus Status,
    string? ChiefComplaint,
    string? Symptoms,
    string? Diagnosis,
    string? ClinicianNotes,
    string? InternalNotes,
    string? PatientSummary,
    DateTimeOffset? SuggestedFollowUpAtUtc,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset LastModifiedAtUtc,
    DateTimeOffset? FinalizedAtUtc,
    byte[] RowVersion,
    IReadOnlyList<AmendmentResponse> Amendments,
    IReadOnlyList<VitalResponse> VitalMeasurements);

public sealed record AmendmentResponse(
    Guid Id,
    string Reason,
    string AmendmentText,
    DateTimeOffset CreatedAtUtc);

public sealed record VitalResponse(
    Guid Id,
    VitalMeasurementType Type,
    VitalUnit Unit,
    int? SystolicMmHg,
    int? DiastolicMmHg,
    int? HeartRateBpm,
    decimal? TemperatureCelsius,
    decimal? OxygenSaturationPercent,
    decimal? WeightKg,
    decimal? HeightCm,
    int? RespiratoryRateBreathsPerMin,
    DateTimeOffset MeasuredAtUtc,
    DateTimeOffset CreatedAtUtc);

public sealed record VisitListItemResponse(
    Guid Id,
    Guid PatientId,
    Guid DoctorId,
    Guid? AppointmentId,
    DateTimeOffset OccurredAtUtc,
    VisitStatus Status);
