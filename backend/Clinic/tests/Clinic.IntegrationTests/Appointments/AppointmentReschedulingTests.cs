using Clinic.Application.Appointments;
using Clinic.Application.Abstractions;
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
    [Fact]
    public async Task Reschedule_SaveTimeConflict_RollsBackAndPreservesCompetingUpdate()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var token = timeout.Token;
        var doctor = new Doctor("Concurrency test doctor");
        var patient = new Patient("Concurrency test patient", "0790000011");
        var start = TestWorkingHours.CreateFutureStart();
        var appointment = new Appointment(patient.Id, doctor.Id, start, start.AddMinutes(30));
        await using (var seed = _database.CreateContext())
        {
            seed.AddRange(doctor, patient, appointment);
            seed.DoctorWorkingPeriods.AddRange(TestWorkingHours.CreatePeriods(doctor.Id));
            await seed.SaveChangesAsync(token);
        }

        byte[]? competingVersion = null;
        await using var context = _database.CreateContext();
        var unitOfWork = new BeforeSaveUnitOfWork(context, async cancellationToken =>
        {
            Assert.Equal(EntityState.Modified, context.Entry(
                Assert.Single(context.ChangeTracker.Entries<Appointment>()).Entity).State);
            Assert.Single(context.ChangeTracker.Entries<AppointmentReschedule>());

            // Deliberately bypass the application lock to exercise EF's save-time
            // concurrency protection against a writer outside the locking protocol.
            await using var competing = _database.CreateContext();
            var current = await competing.Appointments.SingleAsync(
                x => x.Id == appointment.Id, cancellationToken);
            current.Confirm();
            await competing.SaveChangesAsync(cancellationToken);
            competingVersion = current.RowVersion.ToArray();
        });
        var service = new AppointmentReschedulingService(
            new AppointmentRepository(context), new DoctorRepository(context),
            new WorkingScheduleRepository(context, TestWorkingHours.ClinicTimeZone),
            unitOfWork, new SqlBookingTransaction(context), TimeProvider.System);

        var result = await service.RescheduleAsync(new RescheduleAppointmentRequest(
            appointment.Id, doctor.Id, start.AddHours(1), start.AddHours(1).AddMinutes(30),
            "Failed move", appointment.RowVersion.ToArray()), "rescheduling-user", token);

        Assert.Equal(ReschedulingError.AppointmentChanged, result.Error);
        Assert.Null(result.ChangeId);
        Assert.NotNull(competingVersion);
        Assert.Null(context.Database.CurrentTransaction);
        await using var verification = _database.CreateContext();
        var saved = await verification.Appointments.SingleAsync(x => x.Id == appointment.Id, token);
        Assert.Equal(AppointmentStatus.Confirmed, saved.Status);
        Assert.Equal(start, saved.StartsAtUtc);
        Assert.Equal(start.AddMinutes(30), saved.EndsAtUtc);
        Assert.Equal(competingVersion, saved.RowVersion);
        Assert.False(await verification.AppointmentReschedules.AnyAsync(
            x => x.AppointmentId == appointment.Id, token));

        // A fresh operation must acquire the released lock and successfully save.
        var retry = await CreateService(verification).RescheduleAsync(
            new RescheduleAppointmentRequest(appointment.Id, doctor.Id,
                start.AddHours(1), start.AddHours(1).AddMinutes(30), "Retry",
                saved.RowVersion.ToArray()), "retry-user", token);
        Assert.True(retry.IsSuccess);
        await using var final = _database.CreateContext();
        var history = await final.AppointmentReschedules.Where(
            x => x.AppointmentId == appointment.Id).ToListAsync(token);
        Assert.Equal("Retry", Assert.Single(history).Reason);
    }

    private sealed class BeforeSaveUnitOfWork(
        ClinicDbContext context,
        Func<CancellationToken, Task> beforeSave) : IUnitOfWork
    {
        public async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            await beforeSave(cancellationToken);
            return await context.SaveChangesAsync(cancellationToken);
        }
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
