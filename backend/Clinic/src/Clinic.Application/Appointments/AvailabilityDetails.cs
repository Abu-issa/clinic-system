using Clinic.Domain.Enums;

namespace Clinic.Application.Appointments;

public sealed record AppointmentSlot(DateTimeOffset StartsAtUtc, DateTimeOffset EndsAtUtc);
public sealed record WorkingPeriod(TimeOnly StartsAtLocal, TimeOnly EndsAtLocal);
public sealed record WorkingDay(bool IsClosed, IReadOnlyList<WorkingPeriod> Periods);

public sealed record AvailabilityDetails(
    DateOnly LocalDate,
    string TimeZoneId,
    AppointmentType? AppointmentType,
    double DurationMinutes,
    IReadOnlyList<AppointmentSlot> Slots);

public sealed record AvailabilityResult(BookingError Error, AvailabilityDetails? Details = null);
