namespace Clinic.Api.Appointments;

public sealed record RescheduleAppointmentResponse(Guid ChangeId, byte[] RowVersion);
