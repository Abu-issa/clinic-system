using Clinic.Application.Appointments;
using Clinic.Domain.Entities;
using Clinic.Infrastructure.Persistence;
using Clinic.Infrastructure.Repositories;
using Clinic.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Clinic.IntegrationTests.Appointments;

public sealed class BookingDayClosureTests :
    IClassFixture<SqlDatabaseFixture>
{
    private readonly SqlDatabaseFixture _database;

    public BookingDayClosureTests(SqlDatabaseFixture database)
    {
        _database = database;
    }

    [Fact]
    public async Task BookAsync_WhenDoctorDayIsClosed_DoesNotSaveAppointment()
    {
        var doctor = new Doctor("طبيب اختبار إغلاق الحجز");
        var patient = new Patient(
            "مريض تجريبي",
            "+962790000001");

        var startsAtUtc = TestWorkingHours.CreateFutureStart();
        var endsAtUtc = startsAtUtc.AddMinutes(30);

        var localStart = TimeZoneInfo.ConvertTime(
            startsAtUtc,
            TestWorkingHours.ClinicTimeZone);

        var closureDate = DateOnly.FromDateTime(
            localStart.DateTime);

        await using (var seed = _database.CreateContext())
        {
            seed.Doctors.Add(doctor);
            seed.Patients.Add(patient);

            seed.DoctorWorkingPeriods.AddRange(
                TestWorkingHours.CreatePeriods(doctor.Id));

            seed.DoctorDayClosures.Add(
                new DoctorDayClosure(
                    doctor.Id,
                    closureDate,
                    "إجازة تجريبية"));

            await seed.SaveChangesAsync();
        }

        await using (var context = _database.CreateContext())
        {
            var service = new AppointmentBookingService(
                new PatientRepository(context),
                new DoctorRepository(context),
                new AppointmentRepository(context),
                context,
                new SqlBookingTransaction(context),
                new WorkingScheduleRepository(
                    context,
                    TestWorkingHours.ClinicTimeZone),
                TimeProvider.System);

            var request = new BookAppointmentRequest(
                patient.Id,
                doctor.Id,
                startsAtUtc,
                endsAtUtc);

            var result = await service.BookAsync(request);

            Assert.False(result.IsSuccess);
            Assert.Equal(
                BookingError.OutsideWorkingHours,
                result.Error);
            Assert.Null(result.AppointmentId);
        }

        await using var verification = _database.CreateContext();

        var hasAppointment = await verification.Appointments
            .AnyAsync(x => x.DoctorId == doctor.Id);

        Assert.False(hasAppointment);
    }
}
