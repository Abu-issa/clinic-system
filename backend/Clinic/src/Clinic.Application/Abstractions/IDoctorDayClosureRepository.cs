using Clinic.Domain.Entities;

namespace Clinic.Application.Abstractions;

public interface IDoctorDayClosureRepository
{
    Task<bool> ExistsAsync(
        Guid doctorId,
        DateOnly localDate,
        CancellationToken cancellationToken = default);

    Task AddAsync(
        DoctorDayClosure closure,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Appointment>> GetAffectedAppointmentsAsync(
        Guid doctorId,
        DateTimeOffset dayStartsAtUtc,
        DateTimeOffset dayEndsAtUtc,
        CancellationToken cancellationToken = default);
}
