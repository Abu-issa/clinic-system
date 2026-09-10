namespace Clinic.Application.Schedules;

public sealed record CloseDoctorDayRequest(
    Guid DoctorId,
    DateOnly LocalDate,
    string Reason);
