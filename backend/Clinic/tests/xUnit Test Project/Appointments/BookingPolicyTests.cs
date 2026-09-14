using Clinic.Application.Appointments;
using Clinic.Domain.Enums;

namespace Clinic.UnitTests.Appointments;

public sealed class BookingPolicyTests
{
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Amman");
    private static readonly BookingPolicy Policy = new(new BookingPolicySettings(), Zone);
    private static readonly DateOnly Date = new(2030, 1, 7);
    private static DateTimeOffset At(DateOnly date, int hour, int minute = 0) =>
        new(TimeZoneInfo.ConvertTimeToUtc(date.ToDateTime(new TimeOnly(hour, minute)), Zone));
    private static WorkingDay Day(params WorkingPeriod[] periods) => new(false, periods);
    private static WorkingPeriod Period(int startHour, int endHour) => new(new(startHour, 0), new(endHour, 0));

    [Theory]
    [InlineData(AppointmentType.Consultation, 30, 3)]
    [InlineData(AppointmentType.FollowUp, 15, 4)]
    public void Types_HaveConfiguredDurationsAndInclusivePeriodEnd(AppointmentType type, int minutes, int count)
    {
        var slots = Policy.Generate(Day(Period(9, 10)), Date, Policy.Duration(type), [], At(Date, 7));
        Assert.Equal(count, slots.Count);
        Assert.All(slots, s => Assert.Equal(TimeSpan.FromMinutes(minutes), s.EndsAtUtc - s.StartsAtUtc));
        Assert.Equal(At(Date, 10), slots[^1].EndsAtUtc);
    }

    [Fact]
    public void MultiplePeriodsGapsOverlapsAndDuplicates_AreHandled()
    {
        var day = Day(Period(13, 14), Period(9, 10), Period(9, 10));
        var occupied = new[] { new AppointmentSlot(At(Date, 9, 15), At(Date, 9, 45)) };
        var slots = Policy.Generate(day, Date, TimeSpan.FromMinutes(15), occupied, At(Date, 7));
        Assert.Equal(new[] { At(Date, 9), At(Date, 9, 45), At(Date, 13), At(Date, 13, 15), At(Date, 13, 30), At(Date, 13, 45) },
            slots.Select(s => s.StartsAtUtc));
        Assert.Equal(slots.Count, slots.Distinct().Count());
        Assert.Empty(Policy.Generate(new WorkingDay(true, day.Periods), Date, TimeSpan.FromMinutes(15), [], At(Date, 7)));
        Assert.Empty(Policy.Generate(Day(), Date, TimeSpan.FromMinutes(15), [], At(Date, 7)));
        Assert.Equal(BookingError.OutsideWorkingHours, Policy.ValidateSlot(day, At(Date, 9, 45), At(Date, 13, 15), At(Date, 7)));
    }

    [Fact]
    public void Notice_IsInclusive_AndHorizonUsesLocalCalendarDates()
    {
        var now = At(Date, 0, 30);
        Assert.NotEqual(Date, DateOnly.FromDateTime(now.UtcDateTime));
        Assert.Equal(BookingError.None, Policy.ValidateWindow(now.AddMinutes(60), now));
        Assert.Equal(BookingError.InsufficientNotice, Policy.ValidateWindow(now.AddMinutes(60).AddTicks(-1), now));
        Assert.Equal(BookingError.None, Policy.ValidateDate(Date, now));
        Assert.Equal(BookingError.None, Policy.ValidateDate(Date.AddDays(30), now));
        Assert.Equal(BookingError.OutsideBookingWindow, Policy.ValidateDate(Date.AddDays(31), now));
        Assert.Equal(BookingError.OutsideBookingWindow, Policy.ValidateDate(Date.AddDays(-1), now));
        Assert.Equal(BookingError.InvalidDate, Policy.ValidateDate(default, now));
        Assert.Equal(BookingError.InvalidDate, Policy.ValidateDate(DateOnly.MaxValue, now));
        Assert.Equal(BookingError.None, Policy.ValidateWindow(At(Date.AddDays(30), 23, 59), now));
        Assert.Equal(BookingError.OutsideBookingWindow, Policy.ValidateWindow(At(Date.AddDays(31), 0), now));
    }

    [Fact]
    public void Grid_IsRelativeToEachPeriod_NotMidnight_AndRejectsSubMinuteStarts()
    {
        var day = Day(new WorkingPeriod(new(9, 5), new(10, 5)));
        Assert.Equal(BookingError.None, Policy.ValidateSlot(day, At(Date, 9, 20), At(Date, 9, 50), At(Date, 7)));
        Assert.Equal(BookingError.OffGrid, Policy.ValidateSlot(day, At(Date, 9, 15), At(Date, 9, 45), At(Date, 7)));
        Assert.Equal(BookingError.OffGrid, Policy.ValidateSlot(day, At(Date, 9, 20).AddTicks(1), At(Date, 9, 50).AddTicks(1), At(Date, 7)));
        Assert.Equal(new[] { At(Date, 9, 5), At(Date, 9, 20), At(Date, 9, 35) },
            Policy.Generate(day, Date, TimeSpan.FromMinutes(30), [], At(Date, 7)).Select(s => s.StartsAtUtc));
    }

    [Fact]
    public void ConfigurationChangesAffectNewDurations_AndStoredDurationCanStillGenerateSlots()
    {
        var changed = new BookingPolicy(new BookingPolicySettings
        {
            ConsultationMinutes = 45, FollowUpMinutes = 20, SlotStartIntervalMinutes = 10
        }, Zone);
        Assert.Equal(TimeSpan.FromMinutes(45), changed.Duration(AppointmentType.Consultation));
        Assert.Equal(TimeSpan.FromMinutes(20), changed.Duration(AppointmentType.FollowUp));
        Assert.Throws<ArgumentOutOfRangeException>(() => changed.Duration((AppointmentType)99));
        var slots = changed.Generate(Day(Period(9, 10)), Date, TimeSpan.FromMinutes(30), [], At(Date, 7));
        Assert.Equal(4, slots.Count);
        Assert.All(slots, slot => Assert.Equal(TimeSpan.FromMinutes(30), slot.EndsAtUtc - slot.StartsAtUtc));
    }

    [Theory]
    [InlineData(0, 15, 15, 60, 30)]
    [InlineData(30, -1, 15, 60, 30)]
    [InlineData(30, 15, 0, 60, 30)]
    [InlineData(30, 15, 15, -1, 30)]
    [InlineData(30, 15, 15, 60, -1)]
    [InlineData(1441, 15, 15, 60, 30)]
    [InlineData(30, 15, 15, 60, 367)]
    public void InvalidConfiguration_IsRejected(int consultation, int followUp, int interval, int notice, int horizon)
    {
        var settings = new BookingPolicySettings { ConsultationMinutes = consultation, FollowUpMinutes = followUp,
            SlotStartIntervalMinutes = interval, MinimumAdvanceNoticeMinutes = notice, BookingHorizonDays = horizon };
        Assert.False(settings.IsValid());
        Assert.Throws<ArgumentException>(() => new BookingPolicy(settings, Zone));
    }

    [Fact]
    public void InvalidAndAmbiguousLocalTimes_AreSkippedAndRejected()
    {
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(new DateTime(2030, 1, 1), new DateTime(2030, 12, 31),
            TimeSpan.FromHours(1),
            TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 10),
            TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 3, 0, 0), 10, 10));
        var zone = TimeZoneInfo.CreateCustomTimeZone("SyntheticDST", TimeSpan.Zero, "Synthetic", "Standard", "Daylight", [rule]);
        var policy = new BookingPolicy(new BookingPolicySettings(), zone);
        foreach (var date in new[] { new DateOnly(2030, 3, 10), new DateOnly(2030, 10, 10) })
        {
            Assert.False(policy.TryUtc(date.ToDateTime(new TimeOnly(2, 15)), out _));
            var now = new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).AddDays(-1);
            var slots = policy.Generate(Day(Period(1, 5)), date, TimeSpan.FromMinutes(30), [], now);
            Assert.NotEmpty(slots);
            Assert.All(slots, s =>
            {
                Assert.True(policy.TryUtc(TimeZoneInfo.ConvertTime(s.StartsAtUtc, zone).DateTime, out _));
                Assert.True(policy.TryUtc(TimeZoneInfo.ConvertTime(s.EndsAtUtc, zone).DateTime, out _));
            });
        }
        var ambiguous = new DateTimeOffset(2030, 10, 10, 2, 15, 0, TimeSpan.Zero);
        Assert.Equal(BookingError.InvalidLocalTime, policy.ValidateSlot(Day(Period(1, 5)), ambiguous,
            ambiguous.AddMinutes(15), ambiguous.AddDays(-1)));
    }
}
