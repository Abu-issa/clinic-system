using Clinic.Application.Appointments;
using Clinic.Domain.Entities;
using Clinic.Infrastructure.Persistence;
using Clinic.Infrastructure.Repositories;
using Clinic.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Clinic.IntegrationTests.Appointments;

public sealed class BookingConcurrencyTests :
    IClassFixture<SqlDatabaseFixture>
{
    private readonly SqlDatabaseFixture _database;

    public BookingConcurrencyTests(SqlDatabaseFixture database)
    {
        _database = database;
    }

    [Theory]
    [InlineData(0, 30)]
    [InlineData(15, 45)]
    [InlineData(-15, 45)]
    public async Task ConcurrentOverlappingBookings_SaveOnlyOne(
        int secondStartMinutes,
        int secondEndMinutes)
    {
        var doctor = new Doctor("طبيب اختبار التزامن");
        var patient = new Patient("مريض تجريبي", "0790000000");

        await SeedAsync(patient, doctor);

        var start = DateTimeOffset.UtcNow.AddDays(7);

        var firstRequest = new BookAppointmentRequest(
            patient.Id,
            doctor.Id,
            start,
            start.AddMinutes(30));

        var secondRequest = new BookAppointmentRequest(
            patient.Id,
            doctor.Id,
            start.AddMinutes(secondStartMinutes),
            start.AddMinutes(secondEndMinutes));

        var results = await BookTogetherAsync(
            firstRequest,
            secondRequest);

        var success = Assert.Single(
            results.Where(result => result.IsSuccess));

        var failure = Assert.Single(
            results.Where(result => !result.IsSuccess));

        Assert.Equal(
            BookingError.TimeSlotUnavailable,
            failure.Error);

        await using var verification = _database.CreateContext();

        var saved = await verification.Appointments
            .AsNoTracking()
            .Where(appointment => appointment.DoctorId == doctor.Id)
            .ToListAsync();

        var appointment = Assert.Single(saved);

        Assert.Equal<Guid?>(appointment.Id, success.AppointmentId);
    }
    [Fact]
    public async Task Transaction_WhenOperationFails_RollsBackAndReleasesLock()
    {
        var doctor = new Doctor("طبيب اختبار التراجع");
        var patient = new Patient("مريض اختبار التراجع", "0790000004");

        await SeedAsync(patient, doctor);

        var start = DateTimeOffset.UtcNow.AddDays(7);

        var request = new BookAppointmentRequest(
            patient.Id,
            doctor.Id,
            start,
            start.AddMinutes(30));

        // Save inside a transaction, then fail before commit.
        await using (var failingContext = _database.CreateContext())
        {
            var transaction = new SqlBookingTransaction(failingContext);

            var exception =
                await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                {
                    await transaction.ExecuteAsync<bool>(
                        doctor.Id,
                        async token =>
                        {
                            var appointment = new Appointment(
                                patient.Id,
                                doctor.Id,
                                request.StartsAt,
                                request.EndsAt);

                            failingContext.Appointments.Add(appointment);

                            await failingContext.SaveChangesAsync(token);

                            throw new InvalidOperationException(
                                "Simulated failure before commit.");
                        });
                });

            Assert.Equal(
                "Simulated failure before commit.",
                exception.Message);
        }

        // A new context must not see the rolled-back appointment.
        await using (var verification = _database.CreateContext())
        {
            var exists = await verification.Appointments.AnyAsync(
                appointment => appointment.DoctorId == doctor.Id);

            Assert.False(exists);
        }

        // Booking again must succeed after the failed transaction.
        BookAppointmentResult result;

        await using (var retryContext = _database.CreateContext())
        {
            var service = CreateService(retryContext);

            result = await service.BookAsync(request);
        }

        Assert.True(result.IsSuccess);

        await using (var finalVerification = _database.CreateContext())
        {
            var appointments = await finalVerification.Appointments
                .AsNoTracking()
                .Where(appointment => appointment.DoctorId == doctor.Id)
                .ToListAsync();

            var saved = Assert.Single(appointments);

            Assert.Equal<Guid?>(saved.Id, result.AppointmentId);
        }
    }

    [Fact]
    public async Task ConcurrentAdjacentBookings_SaveBoth()
    {
        var doctor = new Doctor("طبيب اختبار المواعيد المتجاورة");
        var patient = new Patient("مريض تجريبي", "0790000001");

        await SeedAsync(patient, doctor);

        var start = DateTimeOffset.UtcNow.AddDays(7);

        var firstRequest = new BookAppointmentRequest(
            patient.Id,
            doctor.Id,
            start,
            start.AddMinutes(30));

        var secondRequest = new BookAppointmentRequest(
            patient.Id,
            doctor.Id,
            start.AddMinutes(30),
            start.AddMinutes(60));

        var results = await BookTogetherAsync(
            firstRequest,
            secondRequest);

        Assert.All(results, result => Assert.True(result.IsSuccess));

        await using var verification = _database.CreateContext();

        var count = await verification.Appointments.CountAsync(
            appointment => appointment.DoctorId == doctor.Id);

        Assert.Equal(2, count);
    }

    [Fact]
    public async Task ConcurrentBookings_ForDifferentDoctors_SaveBoth()
    {
        var firstDoctor = new Doctor("طبيب تجريبي أول");
        var secondDoctor = new Doctor("طبيب تجريبي ثان");

        var firstPatient = new Patient("مريض تجريبي أول", "0790000002");
        var secondPatient = new Patient("مريض تجريبي ثان", "0790000003");

        await SeedAsync(
            firstPatient,
            firstDoctor,
            secondPatient,
            secondDoctor);

        var start = DateTimeOffset.UtcNow.AddDays(7);

        var firstRequest = new BookAppointmentRequest(
            firstPatient.Id,
            firstDoctor.Id,
            start,
            start.AddMinutes(30));

        var secondRequest = new BookAppointmentRequest(
            secondPatient.Id,
            secondDoctor.Id,
            start,
            start.AddMinutes(30));

        var results = await BookTogetherAsync(
            firstRequest,
            secondRequest);

        Assert.All(results, result => Assert.True(result.IsSuccess));

        await using var verification = _database.CreateContext();

        Assert.Equal(
            1,
            await verification.Appointments.CountAsync(
                appointment => appointment.DoctorId == firstDoctor.Id));

        Assert.Equal(
            1,
            await verification.Appointments.CountAsync(
                appointment => appointment.DoctorId == secondDoctor.Id));
    }

    private async Task SeedAsync(params object[] entities)
    {
        await using var context = _database.CreateContext();

        context.AddRange(entities);

        await context.SaveChangesAsync();
    }

    private async Task<BookAppointmentResult[]> BookTogetherAsync(
        BookAppointmentRequest firstRequest,
        BookAppointmentRequest secondRequest)
    {
        using var timeout =
            new CancellationTokenSource(TimeSpan.FromSeconds(45));

        var startGate = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var firstTask = BookAfterSignalAsync(
            firstRequest,
            startGate.Task,
            timeout.Token);

        var secondTask = BookAfterSignalAsync(
            secondRequest,
            startGate.Task,
            timeout.Token);

        startGate.SetResult(true);

        return await Task.WhenAll(firstTask, secondTask);
    }

    private async Task<BookAppointmentResult> BookAfterSignalAsync(
        BookAppointmentRequest request,
        Task startSignal,
        CancellationToken cancellationToken)
    {
        await using var context = _database.CreateContext();

        var service = CreateService(context);

        await startSignal.WaitAsync(cancellationToken);

        return await service.BookAsync(request, cancellationToken);
    }

    private static AppointmentBookingService CreateService(
        ClinicDbContext context)
    {
        return new AppointmentBookingService(
            new PatientRepository(context),
            new DoctorRepository(context),
            new AppointmentRepository(context),
            context,
            new SqlBookingTransaction(context),
            TimeProvider.System);
    }
}
