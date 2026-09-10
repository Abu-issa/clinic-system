using Clinic.Application.Abstractions;
using Clinic.Domain.Entities;
using Clinic.Domain.Enums;
using Clinic.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Clinic.Infrastructure.Repositories;

public sealed class DoctorDayClosureRepository :
    IDoctorDayClosureRepository
{
    private readonly ClinicDbContext _context;

    public DoctorDayClosureRepository(ClinicDbContext context)
    {
        _context = context;
    }

    public Task<bool> ExistsAsync(
        Guid doctorId,
        DateOnly localDate,
        CancellationToken cancellationToken = default)
    {
        return _context.DoctorDayClosures.AnyAsync(
            closure =>
                closure.DoctorId == doctorId &&
                closure.LocalDate == localDate,
            cancellationToken);
    }

    public Task AddAsync(
        DoctorDayClosure closure,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(closure);
        cancellationToken.ThrowIfCancellationRequested();

        _context.DoctorDayClosures.Add(closure);

        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<Appointment>>
        GetAffectedAppointmentsAsync(
            Guid doctorId,
            DateTimeOffset dayStartsAtUtc,
            DateTimeOffset dayEndsAtUtc,
            CancellationToken cancellationToken = default)
    {
        if (dayEndsAtUtc <= dayStartsAtUtc)
        {
            throw new ArgumentException(
                "Day end must be after day start.",
                nameof(dayEndsAtUtc));
        }

        return await _context.Appointments
            .AsNoTracking()
            .Where(appointment =>
                appointment.DoctorId == doctorId &&
                appointment.StartsAtUtc < dayEndsAtUtc &&
                appointment.EndsAtUtc > dayStartsAtUtc &&
                (
                    appointment.Status == AppointmentStatus.Pending ||
                    appointment.Status == AppointmentStatus.Confirmed ||
                    appointment.Status == AppointmentStatus.Arrived ||
                    appointment.Status == AppointmentStatus.InProgress
                ))
            .OrderBy(appointment => appointment.StartsAtUtc)
            .ThenBy(appointment => appointment.Id)
            .ToListAsync(cancellationToken);
    }
}
