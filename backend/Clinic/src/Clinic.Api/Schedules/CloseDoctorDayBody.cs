namespace Clinic.Api.Schedules;

public sealed record CloseDoctorDayBody(
    DateOnly LocalDate,
    string Reason);
