namespace Clinic.Application.Abstractions;

public interface IWorkingScheduleRepository
{
    Task<bool> IsWithinActivePeriodAsync(
        Guid doctorId,
        DateTimeOffset startsAtUtc,
        DateTimeOffset endsAtUtc,
        CancellationToken cancellationToken = default);
}
