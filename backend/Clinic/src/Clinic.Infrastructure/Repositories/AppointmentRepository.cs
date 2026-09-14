using Clinic.Application.Abstractions;
using Clinic.Application.Appointments;
using Clinic.Domain.Entities;
using Clinic.Domain.Enums;
using Clinic.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Clinic.Infrastructure.Repositories;

public sealed class AppointmentRepository : IAppointmentRepository
{
    public async Task<IReadOnlyList<AppointmentSlot>> GetBlockingIntervalsAsync(
        Guid doctorId, DateTimeOffset fromUtc, DateTimeOffset toUtc,
        Guid? excludedAppointmentId = null, CancellationToken cancellationToken = default)
    {
        return await BlockingAppointments(doctorId).AsNoTracking()
            .Where(x =>
                (!excludedAppointmentId.HasValue || x.Id != excludedAppointmentId.Value) &&
                x.StartsAtUtc < toUtc && x.EndsAtUtc > fromUtc)
            .Select(x => new AppointmentSlot(x.StartsAtUtc, x.EndsAtUtc))
            .ToListAsync(cancellationToken);
    }

    private readonly ClinicDbContext _context;

    private IQueryable<Appointment> BlockingAppointments(Guid doctorId) =>
        _context.Appointments.Where(x => x.DoctorId == doctorId && x.Status != AppointmentStatus.Cancelled);

    public AppointmentRepository(ClinicDbContext context)
    {
        _context = context;
    }

    public Task<bool> HasOverlapAsync(
        Guid doctorId,
        DateTimeOffset startsAtUtc,
        DateTimeOffset endsAtUtc,
        CancellationToken cancellationToken = default)
    {
        return BlockingAppointments(doctorId).AnyAsync(
            appointment =>
                appointment.StartsAtUtc < endsAtUtc &&
                appointment.EndsAtUtc > startsAtUtc,
            cancellationToken);
    }

    public Task AddAsync(
        Appointment appointment,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        _context.Appointments.Add(appointment);

        return Task.CompletedTask;
    }
    public Task<Appointment?> GetForDoctorAsync(
    Guid appointmentId,
    Guid doctorId,
    CancellationToken cancellationToken = default)
    {
        return _context.Appointments
            .AsTracking()
            .SingleOrDefaultAsync(
                appointment =>
                    appointment.Id == appointmentId &&
                    appointment.DoctorId == doctorId,
                cancellationToken);
    }
    public Task<bool> HasOverlapExcludingAsync(
    Guid doctorId,
    Guid excludedAppointmentId,
    DateTimeOffset startsAtUtc,
    DateTimeOffset endsAtUtc,
    CancellationToken cancellationToken = default)
    {
        return BlockingAppointments(doctorId).AnyAsync(
            appointment =>
                appointment.Id != excludedAppointmentId &&
                appointment.StartsAtUtc < endsAtUtc &&
                appointment.EndsAtUtc > startsAtUtc,
            cancellationToken);
    }

    public Task AddRescheduleAsync(
        AppointmentReschedule change,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(change);
        cancellationToken.ThrowIfCancellationRequested();

        _context.AppointmentReschedules.Add(change);

        return Task.CompletedTask;
    }
}
