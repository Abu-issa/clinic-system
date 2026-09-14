using Clinic.Domain.Enums;

namespace Clinic.Application.Appointments;

public sealed record BookAppointmentRequest(
    Guid PatientId,
    Guid DoctorId,
    DateTimeOffset StartsAt,
    AppointmentType AppointmentType);
