using Clinic.Application.Abstractions;
using Clinic.Application.Appointments;
using Clinic.Domain.Entities;
using Clinic.Domain.Enums;
using Xunit;

namespace Clinic.UnitTests.Appointments;

public class AppointmentBookingServiceTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task BookAsync_WhenValid_SavesAppointment()
    {
        var store = new FakeStore();
        var service = CreateService(store);
        var request = CreateRequest();

        var result = await service.BookAsync(request);

        Assert.True(result.IsSuccess);
        Assert.Equal(BookingError.None, result.Error);

        var appointment =
            Assert.IsType<Appointment>(store.AddedAppointment);

        Assert.Equal<Guid?>(appointment.Id, result.AppointmentId);
        Assert.Equal(request.PatientId, appointment.PatientId);
        Assert.Equal(request.StartsAt, appointment.StartsAtUtc);
        Assert.Equal(request.EndsAt, appointment.EndsAtUtc);
        Assert.Equal(AppointmentStatus.Scheduled, appointment.Status);

        Assert.Equal(1, store.SaveCalls);
    }

    [Theory]
    [InlineData(BookingError.InvalidPatientId)]
    [InlineData(BookingError.InvalidTimeRange)]
    [InlineData(BookingError.StartMustBeInFuture)]
    [InlineData(BookingError.PatientNotFound)]
    [InlineData(BookingError.TimeSlotUnavailable)]
    public async Task BookAsync_WhenRejected_DoesNotSave(
        BookingError expectedError)
    {
        var store = new FakeStore();
        var service = CreateService(store);
        var request = CreateRequest();

        switch (expectedError)
        {
            case BookingError.InvalidPatientId:
                request = request with { PatientId = Guid.Empty };
                break;

            case BookingError.InvalidTimeRange:
                request = request with { EndsAt = request.StartsAt };
                break;

            case BookingError.StartMustBeInFuture:
                request = request with
                {
                    StartsAt = Now,
                    EndsAt = Now.AddMinutes(30)
                };
                break;

            case BookingError.PatientNotFound:
                store.PatientExists = false;
                break;

            case BookingError.TimeSlotUnavailable:
                store.HasOverlap = true;
                break;
        }

        var result = await service.BookAsync(request);

        Assert.False(result.IsSuccess);
        Assert.Equal(expectedError, result.Error);
        Assert.Null(result.AppointmentId);
        Assert.Null(store.AddedAppointment);
        Assert.Equal(0, store.SaveCalls);
    }

    [Fact]
    public async Task BookAsync_WhenSaveFails_DoesNotReturnSuccess()
    {
        var store = new FakeStore { FailOnSave = true };
        var service = CreateService(store);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await service.BookAsync(CreateRequest());
        });
    }

    private static AppointmentBookingService CreateService(
        FakeStore store)
    {
        return new AppointmentBookingService(
            store,
            store,
            store,
            new FixedTimeProvider(Now));
    }

    private static BookAppointmentRequest CreateRequest()
    {
        return new BookAppointmentRequest(
            Guid.NewGuid(),
            Now.AddHours(1),
            Now.AddHours(1).AddMinutes(30));
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;

        public FixedTimeProvider(DateTimeOffset now)
        {
            _now = now;
        }

        public override DateTimeOffset GetUtcNow()
        {
            return _now;
        }
    }

    private sealed class FakeStore :
        IPatientRepository,
        IAppointmentRepository,
        IUnitOfWork
    {
        public bool PatientExists { get; set; } = true;

        public bool HasOverlap { get; set; }

        public bool FailOnSave { get; set; }

        public Appointment? AddedAppointment { get; private set; }

        public int SaveCalls { get; private set; }

        public Task<bool> ExistsAsync(
            Guid patientId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(PatientExists);
        }

        public Task<bool> HasOverlapAsync(
            DateTimeOffset startsAtUtc,
            DateTimeOffset endsAtUtc,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(HasOverlap);
        }

        public Task AddAsync(
            Appointment appointment,
            CancellationToken cancellationToken = default)
        {
            AddedAppointment = appointment;
            return Task.CompletedTask;
        }

        public Task<int> SaveChangesAsync(
            CancellationToken cancellationToken = default)
        {
            SaveCalls++;

            if (FailOnSave)
            {
                throw new InvalidOperationException(
                    "Simulated save failure.");
            }

            return Task.FromResult(1);
        }
    }
}
