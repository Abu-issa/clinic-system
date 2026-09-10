using Clinic.Application.Appointments;
using Clinic.Domain.Entities;
using Clinic.Domain.Enums;
using Clinic.Infrastructure.Persistence;
using Clinic.Infrastructure.Repositories;
using Clinic.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Clinic.IntegrationTests.Appointments;

public sealed class AppointmentCancellationTests :
    IClassFixture<SqlDatabaseFixture>
{
    private readonly SqlDatabaseFixture _database;

    public AppointmentCancellationTests(SqlDatabaseFixture database)
    {
        _database = database;
    }

    [Fact]
    public async Task CancelAsync_PersistsDetails_AndAllowsRebooking()
    {
        var doctor = new Doctor("طبيب اختبار الإلغاء");
        var patient = new Patient(
            "مريض تجريبي",
            "+962790000005");

        var startsAtUtc = TestWorkingHours.CreateFutureStart();
        var endsAtUtc = startsAtUtc.AddMinutes(30);

        var appointment = new Appointment(
            patient.Id,
            doctor.Id,
            startsAtUtc,
            endsAtUtc);

        appointment.Confirm();

        await using (var seed = _database.CreateContext())
        {
            seed.Doctors.Add(doctor);
            seed.Patients.Add(patient);

            seed.DoctorWorkingPeriods.AddRange(
                TestWorkingHours.CreatePeriods(doctor.Id));

            seed.Appointments.Add(appointment);

            await seed.SaveChangesAsync();
        }

        const string actorUserId = "test-receptionist";
        var beforeCancellation = DateTimeOffset.UtcNow;

        await using (var context = _database.CreateContext())
        {
            var service = CreateCancellationService(context);

            var result = await service.CancelAsync(
                new CancelAppointmentRequest(
                    appointment.Id,
                    doctor.Id,
                    "  طلب المريض الإلغاء  "),
                actorUserId);

            Assert.True(result.IsSuccess);
            Assert.Equal(CancellationError.None, result.Error);
        }

        var afterCancellation = DateTimeOffset.UtcNow;
        DateTimeOffset originalCancellationTime;

        await using (var verification = _database.CreateContext())
        {
            var saved = await verification.Appointments
                .SingleAsync(x => x.Id == appointment.Id);

            Assert.Equal(AppointmentStatus.Cancelled, saved.Status);
            Assert.Equal("طلب المريض الإلغاء", saved.CancellationReason);
            Assert.Equal(actorUserId, saved.CancelledByUserId);
            Assert.True(saved.CancelledAtUtc.HasValue);

            originalCancellationTime = saved.CancelledAtUtc.Value;

            Assert.Equal(TimeSpan.Zero, originalCancellationTime.Offset);

            Assert.InRange(
                originalCancellationTime,
                beforeCancellation,
                afterCancellation);

            Assert.Equal(startsAtUtc, saved.StartsAtUtc);
            Assert.Equal(endsAtUtc, saved.EndsAtUtc);
        }

        // محاولة الإلغاء مرة ثانية لا تغيّر بيانات الإلغاء الأصلية.
        await using (var context = _database.CreateContext())
        {
            var result = await CreateCancellationService(context)
                .CancelAsync(
                    new CancelAppointmentRequest(
                        appointment.Id,
                        doctor.Id,
                        "سبب بديل"),
                    "another-test-user");

            Assert.False(result.IsSuccess);
            Assert.Equal(
                CancellationError.AppointmentCannotBeCancelled,
                result.Error);
        }

        Guid replacementId;

        // حجز جديد بنفس الفترة باستخدام خدمة الحجز الفعلية.
        await using (var context = _database.CreateContext())
        {
            var bookingService = new AppointmentBookingService(
                new PatientRepository(context),
                new DoctorRepository(context),
                new AppointmentRepository(context),
                context,
                new SqlBookingTransaction(context),
                new WorkingScheduleRepository(
                    context,
                    TestWorkingHours.ClinicTimeZone),
                TimeProvider.System);

            var result = await bookingService.BookAsync(
                new BookAppointmentRequest(
                    patient.Id,
                    doctor.Id,
                    startsAtUtc,
                    endsAtUtc));

            Assert.True(result.IsSuccess);
            Assert.True(result.AppointmentId.HasValue);

            replacementId = result.AppointmentId.Value;
            Assert.NotEqual(appointment.Id, replacementId);
        }

        await using var finalVerification = _database.CreateContext();

        var appointments = await finalVerification.Appointments
            .Where(x => x.DoctorId == doctor.Id)
            .ToListAsync();

        Assert.Equal(2, appointments.Count);

        var cancelled = Assert.Single(
            appointments,
            x => x.Id == appointment.Id);

        Assert.Equal(AppointmentStatus.Cancelled, cancelled.Status);
        Assert.Equal("طلب المريض الإلغاء", cancelled.CancellationReason);
        Assert.Equal(actorUserId, cancelled.CancelledByUserId);
        Assert.Equal<DateTimeOffset?>(
            originalCancellationTime,
            cancelled.CancelledAtUtc);

        var replacement = Assert.Single(
            appointments,
            x => x.Id == replacementId);

        Assert.Equal(AppointmentStatus.Pending, replacement.Status);
        Assert.Equal(startsAtUtc, replacement.StartsAtUtc);
        Assert.Equal(endsAtUtc, replacement.EndsAtUtc);
        Assert.Null(replacement.CancellationReason);
        Assert.Null(replacement.CancelledByUserId);
        Assert.Null(replacement.CancelledAtUtc);
    }

    private static AppointmentCancellationService
        CreateCancellationService(ClinicDbContext context)
    {
        return new AppointmentCancellationService(
            new AppointmentRepository(context),
            context,
            new SqlBookingTransaction(context),
            TimeProvider.System);
    }
}
