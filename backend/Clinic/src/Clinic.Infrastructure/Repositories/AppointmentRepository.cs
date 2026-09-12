using Clinic.Application.Abstractions;
using Clinic.Domain.Entities;
using Clinic.Domain.Enums;
using Clinic.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Clinic.Infrastructure.Repositories;

public sealed class AppointmentRepository : IAppointmentRepository
{
    private readonly ClinicDbContext _context;

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
        return _context.Appointments.AnyAsync(
            appointment =>
                appointment.DoctorId == doctorId &&
                appointment.Status != AppointmentStatus.Cancelled &&
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
        return _context.Appointments.AnyAsync(
            appointment =>
                appointment.DoctorId == doctorId &&
                appointment.Id != excludedAppointmentId &&
                appointment.Status != AppointmentStatus.Cancelled &&
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
