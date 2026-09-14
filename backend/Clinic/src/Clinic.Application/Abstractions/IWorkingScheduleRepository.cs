namespace Clinic.Application.Abstractions;

public interface IWorkingScheduleRepository
{
    Task<Clinic.Application.Appointments.WorkingDay> GetDayAsync(
        Guid doctorId, DateOnly localDate, CancellationToken cancellationToken = default);

    Task<bool> IsWithinActivePeriodAsync(
        Guid doctorId,
        DateTimeOffset startsAtUtc,
        DateTimeOffset endsAtUtc,
        CancellationToken cancellationToken = default);
}
