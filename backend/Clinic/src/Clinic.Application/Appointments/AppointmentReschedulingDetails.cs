using Clinic.Domain.Enums;

namespace Clinic.Application.Appointments;

public sealed record AppointmentReschedulingDetails(
    Guid AppointmentId,
    Guid DoctorId,
    DateTimeOffset StartsAtUtc,
    DateTimeOffset EndsAtUtc,
    AppointmentStatus Status,
    byte[] RowVersion);
