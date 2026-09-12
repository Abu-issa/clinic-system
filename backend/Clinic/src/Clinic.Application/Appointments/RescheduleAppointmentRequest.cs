namespace Clinic.Application.Appointments;

public sealed record RescheduleAppointmentRequest(
    Guid AppointmentId,
    Guid DoctorId,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    string Reason,
    byte[] ExpectedRowVersion);
