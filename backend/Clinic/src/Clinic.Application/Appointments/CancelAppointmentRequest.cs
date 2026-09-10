namespace Clinic.Application.Appointments;

public sealed record CancelAppointmentRequest(
    Guid AppointmentId,
    Guid DoctorId,
    string Reason);
