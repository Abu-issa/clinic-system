namespace Clinic.Api.Appointments;

public sealed record RescheduleAppointmentBody(
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    string Reason,
    byte[] ExpectedRowVersion);
