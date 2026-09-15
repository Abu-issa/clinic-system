using Clinic.Domain.Entities;
using Clinic.Domain.Enums;

namespace Clinic.Application.Visits;

public enum VisitError { None, InvalidInput, PatientNotFound, DoctorNotFound, AppointmentNotFound, AppointmentMismatch, VisitNotFound, InvalidRowVersion, VisitChanged, InvalidLifecycle }
public sealed record VisitResult(bool IsSuccess, VisitError Error, VisitDetails? Details = null)
{
    public static VisitResult Success(VisitDetails details) => new(true, VisitError.None, details);
    public static VisitResult Failure(VisitError error) => new(false, error);
}
public sealed record CreateVisitRequest(Guid PatientId, Guid DoctorId, Guid? AppointmentId, DateTimeOffset OccurredAtUtc);
public sealed record UpdateVisitRequest(VisitClinicalContent Content, byte[] ExpectedRowVersion);
public sealed record AddVitalRequest(VitalReading Reading, DateTimeOffset MeasuredAtUtc, byte[] ExpectedRowVersion);
public sealed record AddAmendmentRequest(string Reason, string AmendmentText, byte[] ExpectedRowVersion);
public sealed record VisitDetails(Guid Id, Guid PatientId, Guid DoctorId, Guid? AppointmentId, DateTimeOffset OccurredAtUtc,
    VisitStatus Status, string? ChiefComplaint, string? Symptoms, string? Diagnosis, string? ClinicianNotes,
    string? InternalNotes, string? PatientSummary, DateTimeOffset? SuggestedFollowUpAtUtc,
    DateTimeOffset CreatedAtUtc, string CreatedByStaffId, DateTimeOffset LastModifiedAtUtc, string LastModifiedByStaffId,
    DateTimeOffset? FinalizedAtUtc, string? FinalizedByStaffId, byte[] RowVersion,
    IReadOnlyList<AmendmentDetails> Amendments, IReadOnlyList<VitalDetails> VitalMeasurements);
public sealed record AmendmentDetails(Guid Id, string Reason, string AmendmentText, DateTimeOffset CreatedAtUtc, string CreatedByStaffId);
public sealed record VitalDetails(Guid Id, VitalMeasurementType Type, VitalUnit Unit, int? SystolicMmHg, int? DiastolicMmHg,
    int? HeartRateBpm, decimal? TemperatureCelsius, decimal? OxygenSaturationPercent, decimal? WeightKg, decimal? HeightCm,
    int? RespiratoryRateBreathsPerMin, DateTimeOffset MeasuredAtUtc, DateTimeOffset CreatedAtUtc, string CreatedByStaffId);
public sealed record VisitListItem(Guid Id, Guid PatientId, Guid DoctorId, Guid? AppointmentId, DateTimeOffset OccurredAtUtc, VisitStatus Status);
public sealed record VisitListResult(bool IsSuccess, VisitError Error, IReadOnlyList<VisitListItem> Visits);
public sealed record VisitAppointmentReference(Guid PatientId, Guid DoctorId);
