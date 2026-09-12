using Clinic.Application.Appointments;
using Clinic.Domain.Entities;
using Clinic.Domain.Enums;
using Clinic.Infrastructure.Persistence;
using Clinic.Infrastructure.Repositories;
using Clinic.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Clinic.IntegrationTests.Appointments;

public sealed class AppointmentReschedulingTests :
    IClassFixture<SqlDatabaseFixture>
{
    private readonly SqlDatabaseFixture _database;

    public AppointmentReschedulingTests(SqlDatabaseFixture database)
    {
        _database = database;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reschedule_ExcludesItself_ButRejectsOtherAppointments(
        bool hasConflictingAppointment)
    {
        var doctor = new Doctor("طبيب اختبار إعادة الجدولة");
        var patient = new Patient(
            "مريض تجريبي",
            "+962790000009");

        var originalStart = TestWorkingHours.CreateFutureStart();
        var originalEnd = originalStart.AddMinutes(30);

        var appointment = new Appointment(
            patient.Id,
            doctor.Id,
            originalStart,
            originalEnd);

        appointment.Confirm();

        // الوقت الجديد يتداخل مع الوقت القديم للموعد نفسه.
        var newStart = originalStart.AddMinutes(15);
        var newEnd = originalStart.AddMinutes(45);

        Appointment? conflictingAppointment = null;

        if (hasConflictingAppointment)
        {
            // ملاصق للموعد القديم، لكنه يتداخل مع الوقت الجديد.
            conflictingAppointment = new Appointment(
                patient.Id,
                doctor.Id,
                originalEnd,
                originalEnd.AddMinutes(30));
        }

        await using (var seed = _database.CreateContext())
        {
            seed.Doctors.Add(doctor);
            seed.Patients.Add(patient);

            seed.DoctorWorkingPeriods.AddRange(
                TestWorkingHours.CreatePeriods(doctor.Id));

            seed.Appointments.Add(appointment);

            if (conflictingAppointment is not null)
            {
                seed.Appointments.Add(conflictingAppointment);
            }

            await seed.SaveChangesAsync();
        }

        const string actorUserId = "test-receptionist";

        var beforeChange = DateTimeOffset.UtcNow;
        RescheduleAppointmentResult result;

        await using (var context = _database.CreateContext())
        {
            var service = CreateService(context);

            result = await service.RescheduleAsync(
                new RescheduleAppointmentRequest(
                    appointment.Id,
                    doctor.Id,
                    newStart,
                    newEnd,
                    "  طلب المريض تغيير الوقت  ",
                    appointment.RowVersion.ToArray()
                    ),  
                actorUserId);
        }

        var afterChange = DateTimeOffset.UtcNow;

        // سياق جديد للتحقق من البيانات المحفوظة بعد المعاملة.
        await using var verification = _database.CreateContext();

        var savedAppointment = await verification.Appointments
            .SingleAsync(x => x.Id == appointment.Id);

        var changes = await verification.AppointmentReschedules
            .Where(x => x.AppointmentId == appointment.Id)
            .ToListAsync();

        Assert.Equal(
            AppointmentStatus.Confirmed,
            savedAppointment.Status);

        Assert.Equal(patient.Id, savedAppointment.PatientId);
        Assert.Equal(doctor.Id, savedAppointment.DoctorId);

        if (hasConflictingAppointment)
        {
            Assert.False(result.IsSuccess);

            Assert.Equal(
                ReschedulingError.TimeSlotUnavailable,
                result.Error);

            Assert.Null(result.ChangeId);
            Assert.Empty(changes);

            Assert.Equal(
                originalStart,
                savedAppointment.StartsAtUtc);

            Assert.Equal(
                originalEnd,
                savedAppointment.EndsAtUtc);

            var savedConflict = await verification.Appointments
                .SingleAsync(x => x.Id == conflictingAppointment!.Id);

            Assert.Equal(originalEnd, savedConflict.StartsAtUtc);
            Assert.Equal(
                originalEnd.AddMinutes(30),
                savedConflict.EndsAtUtc);

            Assert.Equal(
                AppointmentStatus.Pending,
                savedConflict.Status);
        }
        else
        {
            Assert.True(result.IsSuccess);
            Assert.Equal(ReschedulingError.None, result.Error);

            Assert.Equal(newStart, savedAppointment.StartsAtUtc);
            Assert.Equal(newEnd, savedAppointment.EndsAtUtc);

            var change = Assert.Single(changes);

            Assert.Equal<Guid?>(change.Id, result.ChangeId);
            Assert.Equal(appointment.Id, change.AppointmentId);

            Assert.Equal(
                originalStart,
                change.PreviousStartsAtUtc);

            Assert.Equal(
                originalEnd,
                change.PreviousEndsAtUtc);

            Assert.Equal(newStart, change.NewStartsAtUtc);
            Assert.Equal(newEnd, change.NewEndsAtUtc);

            Assert.Equal(
                "طلب المريض تغيير الوقت",
                change.Reason);

            Assert.Equal(actorUserId, change.ChangedByUserId);

            Assert.InRange(
                change.ChangedAtUtc,
                beforeChange,
                afterChange);

            Assert.Equal(
                TimeSpan.Zero,
                change.ChangedAtUtc.Offset);
        }
    }
    [Fact]
    public async Task Reschedule_WithStaleVersion_PreservesFirstChange()
    {
        var doctor = new Doctor("طبيب اختبار نسخة الموعد");
        var patient = new Patient(
            "مريض تجريبي",
            "+962790000010");

        var originalStart = TestWorkingHours.CreateFutureStart();
        var originalEnd = originalStart.AddMinutes(30);

        var appointment = new Appointment(
            patient.Id,
            doctor.Id,
            originalStart,
            originalEnd);

        appointment.Confirm();

        byte[] originalVersion;

        await using (var seed = _database.CreateContext())
        {
            seed.Doctors.Add(doctor);
            seed.Patients.Add(patient);

            seed.DoctorWorkingPeriods.AddRange(
                TestWorkingHours.CreatePeriods(doctor.Id));

            seed.Appointments.Add(appointment);

            await seed.SaveChangesAsync();

            originalVersion = appointment.RowVersion.ToArray();
        }

        Assert.Equal(8, originalVersion.Length);

        var firstStart = originalStart.AddHours(1);
        var firstEnd = firstStart.AddMinutes(30);

        Guid firstChangeId;

        await using (var context = _database.CreateContext())
        {
            var result = await CreateService(context).RescheduleAsync(
                new RescheduleAppointmentRequest(
                    appointment.Id,
                    doctor.Id,
                    firstStart,
                    firstEnd,
                    "التعديل الأول",
                    originalVersion.ToArray()),
                "first-test-user");

            Assert.True(result.IsSuccess);
            Assert.True(result.ChangeId.HasValue);

            firstChangeId = result.ChangeId.Value;
        }

        byte[] versionAfterFirstChange;

        await using (var verification = _database.CreateContext())
        {
            var saved = await verification.Appointments
                .SingleAsync(x => x.Id == appointment.Id);

            versionAfterFirstChange = saved.RowVersion.ToArray();

            Assert.False(
                originalVersion.SequenceEqual(versionAfterFirstChange));

            Assert.Equal(firstStart, saved.StartsAtUtc);
            Assert.Equal(firstEnd, saved.EndsAtUtc);
        }

        // طلب آخر مبني على النسخة التي سبقت التعديل الأول.
        await using (var context = _database.CreateContext())
        {
            var secondStart = originalStart.AddHours(2);

            var result = await CreateService(context).RescheduleAsync(
                new RescheduleAppointmentRequest(
                    appointment.Id,
                    doctor.Id,
                    secondStart,
                    secondStart.AddMinutes(30),
                    "تعديل باستخدام نسخة قديمة",
                    originalVersion.ToArray()),
                "second-test-user");

            Assert.False(result.IsSuccess);

            Assert.Equal(
                ReschedulingError.AppointmentChanged,
                result.Error);

            Assert.Null(result.ChangeId);
        }

        await using var finalVerification = _database.CreateContext();

        var finalAppointment = await finalVerification.Appointments
            .SingleAsync(x => x.Id == appointment.Id);

        Assert.Equal(firstStart, finalAppointment.StartsAtUtc);
        Assert.Equal(firstEnd, finalAppointment.EndsAtUtc);

        Assert.Equal(
            AppointmentStatus.Confirmed,
            finalAppointment.Status);

        Assert.True(
            versionAfterFirstChange.SequenceEqual(
                finalAppointment.RowVersion));

        var changes = await finalVerification.AppointmentReschedules
            .Where(x => x.AppointmentId == appointment.Id)
            .ToListAsync();

        var change = Assert.Single(changes);

        Assert.Equal(firstChangeId, change.Id);
        Assert.Equal("التعديل الأول", change.Reason);
        Assert.Equal("first-test-user", change.ChangedByUserId);

        Assert.Equal(originalStart, change.PreviousStartsAtUtc);
        Assert.Equal(originalEnd, change.PreviousEndsAtUtc);
        Assert.Equal(firstStart, change.NewStartsAtUtc);
        Assert.Equal(firstEnd, change.NewEndsAtUtc);
    }
    private static AppointmentReschedulingService CreateService(
        ClinicDbContext context)
    {
        return new AppointmentReschedulingService(
            new AppointmentRepository(context),
            new DoctorRepository(context),
            new WorkingScheduleRepository(
                context,
                TestWorkingHours.ClinicTimeZone),
            context,
            new SqlBookingTransaction(context),
            TimeProvider.System);
    }
}
