using Clinic.Application.Abstractions;
using Clinic.Application.Appointments;
using Clinic.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Clinic.Infrastructure.Repositories;

public sealed class WorkingScheduleRepository :
    IWorkingScheduleRepository
{
    private readonly ClinicDbContext _context;
    private readonly TimeZoneInfo _clinicTimeZone;

    public WorkingScheduleRepository(
        ClinicDbContext context,
        TimeZoneInfo clinicTimeZone)
    {
        _context = context;
        _clinicTimeZone = clinicTimeZone;
    }

    public async Task<bool> IsWithinActivePeriodAsync(
        Guid doctorId,
        DateTimeOffset startsAtUtc,
        DateTimeOffset endsAtUtc,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (doctorId == Guid.Empty || endsAtUtc <= startsAtUtc)
        {
            return false;
        }

        var localStart = TimeZoneInfo.ConvertTime(
            startsAtUtc,
            _clinicTimeZone);

        var localEnd = TimeZoneInfo.ConvertTime(
            endsAtUtc,
            _clinicTimeZone);

        if (localStart.Date != localEnd.Date)
        {
            return false;
        }

        var startTime = TimeOnly.FromDateTime(localStart.DateTime);
        var endTime = TimeOnly.FromDateTime(localEnd.DateTime);

        if (endTime <= startTime)
        {
            return false;
        }

        var localDate = DateOnly.FromDateTime(localStart.DateTime);

        var isClosed = await _context.DoctorDayClosures
            .AnyAsync(
                closure =>
                    closure.DoctorId == doctorId &&
                    closure.LocalDate == localDate,
                cancellationToken);

        if (isClosed)
        {
            return false;
        }

        var dayOfWeek = localStart.DayOfWeek;

        return await _context.DoctorWorkingPeriods
            .AnyAsync(
                period =>
                    period.DoctorId == doctorId &&
                    period.IsActive &&
                    period.DayOfWeek == dayOfWeek &&
                    period.StartsAtLocal <= startTime &&
                    period.EndsAtLocal >= endTime,
                cancellationToken);
    }

    public async Task<WorkingDay> GetDayAsync(Guid doctorId, DateOnly localDate,
        CancellationToken cancellationToken = default)
    {
        var closed = await _context.DoctorDayClosures.AnyAsync(
            x => x.DoctorId == doctorId && x.LocalDate == localDate, cancellationToken);
        var periods = await _context.DoctorWorkingPeriods.AsNoTracking()
            .Where(x => x.DoctorId == doctorId && x.DayOfWeek == localDate.DayOfWeek && x.IsActive)
            .Select(x => new WorkingPeriod(x.StartsAtLocal, x.EndsAtLocal))
            .ToListAsync(cancellationToken);
        return new WorkingDay(closed, periods);
    }
}
