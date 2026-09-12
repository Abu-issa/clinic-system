using Clinic.Domain.Entities;

namespace Clinic.Application.Abstractions;

public interface IAppointmentRepository
{
    Task<bool> HasOverlapAsync(
        Guid doctorId,
        DateTimeOffset startsAtUtc,
        DateTimeOffset endsAtUtc,
        CancellationToken cancellationToken = default);

    Task AddAsync(
        Appointment appointment,
        CancellationToken cancellationToken = default);
    Task<Appointment?> GetForDoctorAsync(
    Guid appointmentId,
    Guid doctorId,
    CancellationToken cancellationToken = default);
    Task<bool> HasOverlapExcludingAsync(
    Guid doctorId,
    Guid excludedAppointmentId,
    DateTimeOffset startsAtUtc,
    DateTimeOffset endsAtUtc,
    CancellationToken cancellationToken = default);

    Task AddRescheduleAsync(
        AppointmentReschedule change,
        CancellationToken cancellationToken = default);
}
