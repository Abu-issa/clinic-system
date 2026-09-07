namespace Clinic.Application.Appointments;

public sealed record BookAppointmentRequest(
    Guid PatientId,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt);
