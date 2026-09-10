using Clinic.Application.Schedules;
using Clinic.Domain.Entities;
using Clinic.Domain.Enums;
using Clinic.Infrastructure.Persistence;
using Clinic.Infrastructure.Repositories;
using Clinic.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Clinic.Application.Appointments;

namespace Clinic.IntegrationTests.Schedules;

public sealed class DoctorDayClosureServiceTests :
    IClassFixture<SqlDatabaseFixture>
{
    private readonly SqlDatabaseFixture _database;

    public DoctorDayClosureServiceTests(SqlDatabaseFixture database)
    {
        _database = database;
    }

    [Fact]
    public async Task CloseAsync_PreservesAppointments_AndRejectsDuplicate()
    {
        var doctor = new Doctor("طبيب اختبار خدمة الإغلاق");
        var patient = new Patient(
            "مريض تجريبي",
            "+962790000001");

        var startsAtUtc = TestWorkingHours.CreateFutureStart();
        var endsAtUtc = startsAtUtc.AddMinutes(30);

        var localStart = TimeZoneInfo.ConvertTime(
            startsAtUtc,
            TestWorkingHours.ClinicTimeZone);

        var localDate = DateOnly.FromDateTime(localStart.DateTime);

        var appointment = new Appointment(
            patient.Id,
            doctor.Id,
            startsAtUtc,
            endsAtUtc);

        appointment.Confirm();

        var cancelledAppointment = new Appointment(
            patient.Id,
            doctor.Id,
            startsAtUtc.AddHours(1),
            endsAtUtc.AddHours(1));

        cancelledAppointment.Cancel(
    "إلغاء تجريبي",
    "test-staff-user",
    DateTimeOffset.UtcNow);

        await using (var seed = _database.CreateContext())
        {
            seed.Doctors.Add(doctor);
            seed.Patients.Add(patient);

            seed.DoctorWorkingPeriods.AddRange(
                TestWorkingHours.CreatePeriods(doctor.Id));

            seed.Appointments.AddRange(
                appointment,
                cancelledAppointment);

            await seed.SaveChangesAsync();
        }

        var request = new CloseDoctorDayRequest(
            doctor.Id,
            localDate,
            "إجازة تجريبية");

        Guid closureId;

        await using (var context = _database.CreateContext())
        {
            var result = await CreateService(context)
                .CloseAsync(request);

            Assert.True(result.IsSuccess);
            Assert.Equal(CloseDoctorDayError.None, result.Error);
            Assert.True(result.ClosureId.HasValue);

            closureId = result.ClosureId.Value;

            Assert.Equal(
                appointment.Id,
                Assert.Single(result.AffectedAppointmentIds));
        }

        // سياق جديد للتأكد من الحفظ الفعلي بعد اكتمال المعاملة.
        await using (var verification = _database.CreateContext())
        {
            var closure = await verification.DoctorDayClosures
                .SingleAsync(x => x.DoctorId == doctor.Id);

            Assert.Equal(closureId, closure.Id);
            Assert.Equal(localDate, closure.LocalDate);
            Assert.Equal(request.Reason, closure.Reason);

            var savedAppointment = await verification.Appointments
                .SingleAsync(x => x.Id == appointment.Id);

            Assert.Equal(
                AppointmentStatus.Confirmed,
                savedAppointment.Status);

            Assert.Equal(startsAtUtc, savedAppointment.StartsAtUtc);
            Assert.Equal(endsAtUtc, savedAppointment.EndsAtUtc);
            Assert.Equal(patient.Id, savedAppointment.PatientId);

            var savedCancelled = await verification.Appointments
                .SingleAsync(x => x.Id == cancelledAppointment.Id);

            Assert.Equal(
                AppointmentStatus.Cancelled,
                savedCancelled.Status);

            var schedule = new WorkingScheduleRepository(
                verification,
                TestWorkingHours.ClinicTimeZone);

            Assert.False(await schedule.IsWithinActivePeriodAsync(
                doctor.Id,
                startsAtUtc,
                endsAtUtc));
        }

        // محاولة ثانية لنفس الطبيب والتاريخ.
        await using (var context = _database.CreateContext())
        {
            var result = await CreateService(context)
                .CloseAsync(request);

            Assert.False(result.IsSuccess);
            Assert.Equal(
                CloseDoctorDayError.AlreadyClosed,
                result.Error);

            Assert.Null(result.ClosureId);
            Assert.Empty(result.AffectedAppointmentIds);
        }

        await using var finalVerification = _database.CreateContext();

        Assert.Equal(
            1,
            await finalVerification.DoctorDayClosures
                .CountAsync(x =>
                    x.DoctorId == doctor.Id &&
                    x.LocalDate == localDate));

        Assert.Equal(
            2,
            await finalVerification.Appointments
                .CountAsync(x => x.DoctorId == doctor.Id));
    }
    [Fact]
    public async Task CloseAsync_AlongsideBooking_PreservesConsistentOutcome()
    {
        using var timeout = new CancellationTokenSource(
            TimeSpan.FromSeconds(45));

        var token = timeout.Token;

        var doctor = new Doctor("طبيب اختبار تزامن الإغلاق");
        var patient = new Patient(
            "مريض اختبار التزامن",
            "+962790000002");

        var startsAtUtc = TestWorkingHours.CreateFutureStart();
        var endsAtUtc = startsAtUtc.AddMinutes(30);

        var localStart = TimeZoneInfo.ConvertTime(
            startsAtUtc,
            TestWorkingHours.ClinicTimeZone);

        var localDate = DateOnly.FromDateTime(localStart.DateTime);

        await using (var seed = _database.CreateContext())
        {
            seed.Doctors.Add(doctor);
            seed.Patients.Add(patient);

            seed.DoctorWorkingPeriods.AddRange(
                TestWorkingHours.CreatePeriods(doctor.Id));

            await seed.SaveChangesAsync(token);
        }

        var startGate = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<BookAppointmentResult> BookAsync()
        {
            await using var context = _database.CreateContext();

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

            await startGate.Task.WaitAsync(token);

            return await service.BookAsync(
                new BookAppointmentRequest(
                    patient.Id,
                    doctor.Id,
                    startsAtUtc,
                    endsAtUtc),
                token);
        }

        async Task<CloseDoctorDayResult> CloseAsync()
        {
            await using var context = _database.CreateContext();

            var service = CreateService(context);

            await startGate.Task.WaitAsync(token);

            return await service.CloseAsync(
                new CloseDoctorDayRequest(
                    doctor.Id,
                    localDate,
                    "إغلاق تجريبي متزامن"),
                token);
        }

        var bookingTask = BookAsync();
        var closureTask = CloseAsync();

        startGate.SetResult(true);

        await Task.WhenAll(bookingTask, closureTask);

        var bookingResult = await bookingTask;
        var closureResult = await closureTask;

        Assert.True(closureResult.IsSuccess);

        await using var verification = _database.CreateContext();

        var savedClosure = await verification.DoctorDayClosures
            .SingleAsync(
                x => x.DoctorId == doctor.Id &&
                     x.LocalDate == localDate,
                token);

        Assert.Equal<Guid?>(
            savedClosure.Id,
            closureResult.ClosureId);

        var appointments = await verification.Appointments
            .Where(x => x.DoctorId == doctor.Id)
            .ToListAsync(token);

        if (bookingResult.IsSuccess)
        {
            var appointment = Assert.Single(appointments);

            Assert.Equal<Guid?>(
                appointment.Id,
                bookingResult.AppointmentId);

            Assert.Equal(
                appointment.Id,
                Assert.Single(closureResult.AffectedAppointmentIds));

            Assert.Equal(
                AppointmentStatus.Pending,
                appointment.Status);

            Assert.Equal(startsAtUtc, appointment.StartsAtUtc);
            Assert.Equal(endsAtUtc, appointment.EndsAtUtc);
        }
        else
        {
            Assert.Equal(
                BookingError.OutsideWorkingHours,
                bookingResult.Error);

            Assert.Null(bookingResult.AppointmentId);
            Assert.Empty(appointments);
            Assert.Empty(closureResult.AffectedAppointmentIds);
        }
    }

    private static DoctorDayClosureService CreateService(
        ClinicDbContext context)
    {
        return new DoctorDayClosureService(
            new DoctorRepository(context),
            new DoctorDayClosureRepository(context),
            context,
            new SqlBookingTransaction(context),
            TestWorkingHours.ClinicTimeZone,
            TimeProvider.System);
    }
}
