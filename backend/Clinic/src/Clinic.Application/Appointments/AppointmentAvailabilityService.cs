using Clinic.Application.Abstractions;
using Clinic.Domain.Enums;

namespace Clinic.Application.Appointments;

public sealed class AppointmentAvailabilityService(
    IDoctorRepository doctors,
    IWorkingScheduleRepository schedules,
    IAppointmentRepository appointments,
    BookingPolicy policy,
    TimeProvider clock)
{
    public Task<AvailabilityResult> GetAsync(Guid doctorId, DateOnly date, AppointmentType type,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(type)) return Task.FromResult(new AvailabilityResult(BookingError.InvalidAppointmentType));
        return GenerateAsync(doctorId, date, type, policy.Duration(type), null, cancellationToken);
    }

    // Only called after doctor-scoped authorization by the protected rescheduling resource.
    // Resolve the duration, type and exclusion from the scoped stored appointment, never client values.
    public async Task<AvailabilityResult> GetForReschedulingAsync(Guid doctorId, Guid appointmentId,
        DateOnly date, CancellationToken cancellationToken = default)
    {
        if (doctorId == Guid.Empty) return new(BookingError.InvalidDoctorId);
        var appointment = await appointments.GetForDoctorAsync(appointmentId, doctorId, cancellationToken);
        if (appointment is null) return new(BookingError.AppointmentNotFound);
        if (appointment.Status is not (AppointmentStatus.Pending or AppointmentStatus.Confirmed))
            return new(BookingError.AppointmentCannotBeRescheduled);
        return await GenerateAsync(doctorId, date, appointment.Type,
            appointment.EndsAtUtc - appointment.StartsAtUtc, appointment.Id, cancellationToken);
    }

    private async Task<AvailabilityResult> GenerateAsync(Guid doctorId, DateOnly date,
        AppointmentType? type, TimeSpan duration, Guid? excludedId, CancellationToken cancellationToken)
    {
        if (doctorId == Guid.Empty) return new(BookingError.InvalidDoctorId);
        var dateError = policy.ValidateDate(date, clock.GetUtcNow());
        if (dateError != BookingError.None) return new(dateError);
        var doctor = await doctors.GetByIdAsync(doctorId, cancellationToken);
        if (doctor is null) return new(BookingError.DoctorNotFound);
        if (!doctor.IsActive) return new(BookingError.DoctorInactive);
        var day = await schedules.GetDayAsync(doctorId, date, cancellationToken);

        // A bounded UTC envelope covers the local date for every legal timezone offset,
        // without guessing how to resolve an ambiguous/nonexistent local midnight.
        var midnight = new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var occupied = day.IsClosed || day.Periods.Count == 0
            ? Array.Empty<AppointmentSlot>()
            : await appointments.GetBlockingIntervalsAsync(doctorId, midnight.AddHours(-14),
                midnight.AddHours(38), excludedId, cancellationToken);
        var slots = policy.Generate(day, date, duration, occupied, clock.GetUtcNow());
        return new(BookingError.None, new AvailabilityDetails(date, policy.TimeZone.Id,
            type, duration.TotalMinutes, slots));
    }
}
