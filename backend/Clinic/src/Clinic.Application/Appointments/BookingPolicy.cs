using Clinic.Domain.Enums;

namespace Clinic.Application.Appointments;

public sealed class BookingPolicy
{
    private readonly BookingPolicySettings _settings;
    public TimeZoneInfo TimeZone { get; }

    public BookingPolicy(BookingPolicySettings settings, TimeZoneInfo timeZone)
    {
        if (!settings.IsValid()) throw new ArgumentException("Invalid booking policy settings.", nameof(settings));
        _settings = settings with { };
        TimeZone = timeZone;
    }

    public TimeSpan Duration(AppointmentType type) => type switch
    {
        AppointmentType.Consultation => TimeSpan.FromMinutes(_settings.ConsultationMinutes),
        AppointmentType.FollowUp => TimeSpan.FromMinutes(_settings.FollowUpMinutes),
        _ => throw new ArgumentOutOfRangeException(nameof(type))
    };

    public DateOnly LocalDate(DateTimeOffset instant) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, TimeZone).DateTime);

    public BookingError ValidateDate(DateOnly date, DateTimeOffset now)
    {
        if (date == default || date == DateOnly.MaxValue) return BookingError.InvalidDate;
        var days = date.DayNumber - LocalDate(now).DayNumber;
        return days < 0 || days > _settings.BookingHorizonDays
            ? BookingError.OutsideBookingWindow : BookingError.None;
    }

    public BookingError ValidateWindow(DateTimeOffset start, DateTimeOffset now)
    {
        if (start <= now) return BookingError.StartMustBeInFuture;
        var error = ValidateDate(LocalDate(start), now);
        if (error != BookingError.None) return error;
        return start - now < TimeSpan.FromMinutes(_settings.MinimumAdvanceNoticeMinutes)
            ? BookingError.InsufficientNotice : BookingError.None;
    }

    public bool TryUtc(DateTime local, out DateTimeOffset utc)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        utc = default;
        if (TimeZone.IsInvalidTime(local) || TimeZone.IsAmbiguousTime(local)) return false;
        try
        {
            utc = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, TimeZone));
            return true;
        }
        catch (ArgumentException) { return false; }
    }

    public BookingError ValidateSlot(WorkingDay day, DateTimeOffset start,
        DateTimeOffset end, DateTimeOffset now)
    {
        var window = ValidateWindow(start, now);
        if (window != BookingError.None) return window;
        if (end <= start) return BookingError.InvalidTimeRange;
        var localStart = TimeZoneInfo.ConvertTime(start, TimeZone).DateTime;
        var localEnd = TimeZoneInfo.ConvertTime(end, TimeZone).DateTime;
        if (!TryUtc(localStart, out var checkedStart) || !TryUtc(localEnd, out var checkedEnd) ||
            checkedStart != start || checkedEnd != end)
            return BookingError.InvalidLocalTime;
        if (day.IsClosed || localStart.Date != localEnd.Date) return BookingError.OutsideWorkingHours;
        var startTime = TimeOnly.FromDateTime(localStart);
        var endTime = TimeOnly.FromDateTime(localEnd);
        var containing = day.Periods.Where(p => p.StartsAtLocal <= startTime && p.EndsAtLocal >= endTime).ToArray();
        if (containing.Length == 0) return BookingError.OutsideWorkingHours;
        var interval = TimeSpan.FromMinutes(_settings.SlotStartIntervalMinutes).Ticks;
        return containing.Any(p => (startTime.Ticks - p.StartsAtLocal.Ticks) % interval == 0)
            ? BookingError.None : BookingError.OffGrid;
    }

    public IReadOnlyList<AppointmentSlot> Generate(WorkingDay day, DateOnly date, TimeSpan duration,
        IReadOnlyList<AppointmentSlot> occupied, DateTimeOffset now)
    {
        if (day.IsClosed || duration <= TimeSpan.Zero || ValidateDate(date, now) != BookingError.None)
            return Array.Empty<AppointmentSlot>();
        var slots = new HashSet<AppointmentSlot>();
        var interval = TimeSpan.FromMinutes(_settings.SlotStartIntervalMinutes).Ticks;
        foreach (var period in day.Periods)
        {
            for (var ticks = period.StartsAtLocal.Ticks; ticks < period.EndsAtLocal.Ticks; ticks += interval)
            {
                var local = date.ToDateTime(new TimeOnly(ticks), DateTimeKind.Unspecified);
                if (!TryUtc(local, out var start) || start > DateTimeOffset.MaxValue - duration) continue;
                var end = start + duration;
                if (ValidateSlot(day, start, end, now) != BookingError.None) continue;
                if (occupied.Any(b => b.StartsAtUtc < end && b.EndsAtUtc > start)) continue;
                slots.Add(new AppointmentSlot(start, end));
            }
        }
        return slots.OrderBy(s => s.StartsAtUtc).ThenBy(s => s.EndsAtUtc).ToArray();
    }
}
