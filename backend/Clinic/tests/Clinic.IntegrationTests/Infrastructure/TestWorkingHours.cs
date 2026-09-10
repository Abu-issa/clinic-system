using Clinic.Domain.Entities;

namespace Clinic.IntegrationTests.Infrastructure;

public static class TestWorkingHours
{
    public static TimeZoneInfo ClinicTimeZone =>
        TimeZoneInfo.FindSystemTimeZoneById("Asia/Amman");

    public static DoctorWorkingPeriod[] CreatePeriods(Guid doctorId)
    {
        return Enum.GetValues<DayOfWeek>()
            .Select(day => new DoctorWorkingPeriod(
                doctorId,
                day,
                new TimeOnly(9, 0),
                new TimeOnly(17, 0)))
            .ToArray();
    }

    public static DateTimeOffset CreateFutureStart()
    {
        var localNow = TimeZoneInfo.ConvertTime(
            DateTimeOffset.UtcNow,
            ClinicTimeZone);

        var localStart = DateTime.SpecifyKind(
            localNow.Date.AddDays(7).AddHours(10),
            DateTimeKind.Unspecified);

        var utcStart = TimeZoneInfo.ConvertTimeToUtc(
            localStart,
            ClinicTimeZone);

        return new DateTimeOffset(utcStart);
    }
}
