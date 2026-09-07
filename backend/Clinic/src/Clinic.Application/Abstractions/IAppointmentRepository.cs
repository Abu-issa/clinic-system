using Clinic.Domain.Entities;

namespace Clinic.Application.Abstractions;

public interface IAppointmentRepository
{
    Task<bool> HasOverlapAsync(
        DateTimeOffset startsAtUtc,
        DateTimeOffset endsAtUtc,
        CancellationToken cancellationToken = default);

    Task AddAsync(
        Appointment appointment,
        CancellationToken cancellationToken = default);
}
