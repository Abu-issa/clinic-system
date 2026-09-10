using Clinic.Domain.Entities;
using Clinic.Infrastructure.Repositories;
using Clinic.IntegrationTests.Infrastructure;
using Xunit;

namespace Clinic.IntegrationTests.Schedules;

public sealed class WorkingScheduleRepositoryTests :
    IClassFixture<SqlDatabaseFixture>
{
    private readonly SqlDatabaseFixture _database;

    public WorkingScheduleRepositoryTests(SqlDatabaseFixture database)
    {
        _database = database;
    }

    [Theory]
    [InlineData(9, 0, 9, 30, true)]
    [InlineData(12, 30, 13, 0, true)]
    [InlineData(8, 45, 9, 15, false)]
    [InlineData(12, 45, 13, 15, false)]
    [InlineData(13, 0, 13, 30, false)]
    [InlineData(12, 30, 16, 30, false)]
    [InlineData(16, 0, 16, 30, true)]
    public async Task Check_RequiresEntireAppointmentWithinOnePeriod(
        int startHour,
        int startMinute,
        int endHour,
        int endMinute,
        bool expected)
    {
        var doctor = new Doctor("طبيب اختبار حدود الدوام");
        var date = new DateTime(2026, 10, 4);

        await using (var seed = _database.CreateContext())
        {
            seed.Doctors.Add(doctor);

            seed.DoctorWorkingPeriods.AddRange(
                new DoctorWorkingPeriod(
                    doctor.Id,
                    date.DayOfWeek,
                    new TimeOnly(9, 0),
                    new TimeOnly(13, 0)),
                new DoctorWorkingPeriod(
                    doctor.Id,
                    date.DayOfWeek,
                    new TimeOnly(16, 0),
                    new TimeOnly(20, 0)));

            await seed.SaveChangesAsync();
        }

        var startsAtUtc = ToUtc(
            date.AddHours(startHour).AddMinutes(startMinute));

        var endsAtUtc = ToUtc(
            date.AddHours(endHour).AddMinutes(endMinute));

        await using var context = _database.CreateContext();

        var repository = new WorkingScheduleRepository(
            context,
            TestWorkingHours.ClinicTimeZone);

        var result = await repository.IsWithinActivePeriodAsync(
            doctor.Id,
            startsAtUtc,
            endsAtUtc);

        Assert.Equal(expected, result);
    }

    [Fact]
    public async Task Check_WhenDoctorHasNoWorkingPeriods_ReturnsFalse()
    {
        var doctor = new Doctor("طبيب بدون دوام");

        await using (var seed = _database.CreateContext())
        {
            seed.Doctors.Add(doctor);
            await seed.SaveChangesAsync();
        }

        var startsAtUtc = ToUtc(new DateTime(2026, 10, 4, 10, 0, 0));

        await using var context = _database.CreateContext();

        var repository = new WorkingScheduleRepository(
            context,
            TestWorkingHours.ClinicTimeZone);

        var result = await repository.IsWithinActivePeriodAsync(
            doctor.Id,
            startsAtUtc,
            startsAtUtc.AddMinutes(30));

        Assert.False(result);
    }

    [Fact]
    public async Task Check_UsesAmmanDayInsteadOfUtcDay()
    {
        var doctor = new Doctor("طبيب اختبار التوقيت");
        var localStart = new DateTime(2026, 10, 4, 0, 30, 0);

        await using (var seed = _database.CreateContext())
        {
            seed.Doctors.Add(doctor);

            seed.DoctorWorkingPeriods.Add(
                new DoctorWorkingPeriod(
                    doctor.Id,
                    localStart.DayOfWeek,
                    new TimeOnly(0, 0),
                    new TimeOnly(2, 0)));

            await seed.SaveChangesAsync();
        }

        var startsAtUtc = ToUtc(localStart);
        var endsAtUtc = ToUtc(localStart.AddMinutes(30));

        Assert.NotEqual(
            localStart.DayOfWeek,
            startsAtUtc.UtcDateTime.DayOfWeek);

        await using var context = _database.CreateContext();

        var repository = new WorkingScheduleRepository(
            context,
            TestWorkingHours.ClinicTimeZone);

        var result = await repository.IsWithinActivePeriodAsync(
            doctor.Id,
            startsAtUtc,
            endsAtUtc);

        Assert.True(result);
    }
    [Theory]
    [InlineData(true, 0, false)]
    [InlineData(true, 1, true)]
    [InlineData(false, 0, true)]
    public async Task Check_ClosureAppliesOnlyToMatchingDoctorAndLocalDate(
    bool closureForSameDoctor,
    int closureDayOffset,
    bool expected)
    {
        var doctor = new Doctor("طبيب اختبار الإغلاق");
        var otherDoctor = new Doctor("طبيب آخر");

        // موعد بعد منتصف الليل بتوقيت عمّان، لاختبار التاريخ المحلي.
        var localStart = new DateTime(2026, 10, 4, 0, 30, 0);
        var appointmentDate = DateOnly.FromDateTime(localStart);

        var closureDoctorId = closureForSameDoctor
            ? doctor.Id
            : otherDoctor.Id;

        await using (var seed = _database.CreateContext())
        {
            seed.Doctors.AddRange(doctor, otherDoctor);

            seed.DoctorWorkingPeriods.Add(
                new DoctorWorkingPeriod(
                    doctor.Id,
                    localStart.DayOfWeek,
                    new TimeOnly(0, 0),
                    new TimeOnly(2, 0)));

            seed.DoctorDayClosures.Add(
                new DoctorDayClosure(
                    closureDoctorId,
                    appointmentDate.AddDays(closureDayOffset),
                    "إجازة تجريبية"));

            await seed.SaveChangesAsync();
        }

        var startsAtUtc = ToUtc(localStart);
        var endsAtUtc = ToUtc(localStart.AddMinutes(30));

        Assert.NotEqual(
            appointmentDate,
            DateOnly.FromDateTime(startsAtUtc.UtcDateTime));

        await using var context = _database.CreateContext();

        var repository = new WorkingScheduleRepository(
            context,
            TestWorkingHours.ClinicTimeZone);

        var result = await repository.IsWithinActivePeriodAsync(
            doctor.Id,
            startsAtUtc,
            endsAtUtc);

        Assert.Equal(expected, result);
    }
    private static DateTimeOffset ToUtc(DateTime localTime)
    {
        var unspecified = DateTime.SpecifyKind(
            localTime,
            DateTimeKind.Unspecified);

        var utc = TimeZoneInfo.ConvertTimeToUtc(
            unspecified,
            TestWorkingHours.ClinicTimeZone);

        return new DateTimeOffset(utc);
    }
}
