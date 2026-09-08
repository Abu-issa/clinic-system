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
        var request = CreateRequest(store.AvailableDoctor!.Id);

        var result = await service.BookAsync(request);

        Assert.True(result.IsSuccess);
        Assert.Equal(BookingError.None, result.Error);
        Assert.Equal<Guid?>(
    request.DoctorId,
    store.LockedDoctorId);

        var appointment =
            Assert.IsType<Appointment>(store.AddedAppointment);
        Assert.Equal(request.DoctorId, appointment.DoctorId);

        Assert.Equal<Guid?>(
            request.DoctorId,
            store.CheckedDoctorId);
        Assert.Equal<Guid?>(appointment.Id, result.AppointmentId);
        Assert.Equal(request.PatientId, appointment.PatientId);
        Assert.Equal(request.StartsAt, appointment.StartsAtUtc);
        Assert.Equal(request.EndsAt, appointment.EndsAtUtc);
        Assert.Equal(AppointmentStatus.Pending, appointment.Status);

        Assert.Equal(1, store.SaveCalls);
    }

    [Theory]
    [InlineData(BookingError.InvalidPatientId)]
    [InlineData(BookingError.InvalidTimeRange)]
    [InlineData(BookingError.StartMustBeInFuture)]
    [InlineData(BookingError.PatientNotFound)]
    [InlineData(BookingError.TimeSlotUnavailable)]
    [InlineData(BookingError.InvalidDoctorId)]
    [InlineData(BookingError.DoctorNotFound)]
    [InlineData(BookingError.DoctorInactive)]
    public async Task BookAsync_WhenRejected_DoesNotSave(
        BookingError expectedError)
    {
        var store = new FakeStore();
        var service = CreateService(store);
        var request = CreateRequest(store.AvailableDoctor!.Id);

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
            case BookingError.InvalidDoctorId:
                request = request with { DoctorId = Guid.Empty };
                break;
            case BookingError.DoctorNotFound:
                store.AvailableDoctor = null;
                break;

            case BookingError.DoctorInactive:
                store.AvailableDoctor!.Deactivate();
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
            await service.BookAsync(CreateRequest(store.AvailableDoctor!.Id));
        });
    }

    private static AppointmentBookingService CreateService(
        FakeStore store)
    {
        return new AppointmentBookingService(
            store,
            store,
            store,
            store,
            store,
            new FixedTimeProvider(Now));
    }

    private static BookAppointmentRequest CreateRequest(
    Guid doctorId)
    {
        return new BookAppointmentRequest(
            Guid.NewGuid(),
            doctorId,
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
    [Fact]
    public async Task BookAsync_WhenLockFails_DoesNotAddOrSave()
    {
        var store = new FakeStore
        {
            FailBeforeOperation = true
        };

        var service = CreateService(store);
        var request = CreateRequest(store.AvailableDoctor!.Id);

        await Assert.ThrowsAsync<TimeoutException>(async () =>
        {
            await service.BookAsync(request);
        });

        Assert.Null(store.CheckedDoctorId);
        Assert.Null(store.AddedAppointment);
        Assert.Equal(0, store.SaveCalls);
    }
    private sealed class FakeStore :
        IPatientRepository,
        IDoctorRepository,
        IAppointmentRepository,
        IUnitOfWork,
        IBookingTransaction
    {
        public Guid? CheckedDoctorId { get; private set; }
        public bool PatientExists { get; set; } = true;

        public bool HasOverlap { get; set; }

        public bool FailOnSave { get; set; }

        public Appointment? AddedAppointment { get; private set; }

        public int SaveCalls { get; private set; }
        public Doctor? AvailableDoctor { get; set; } =
    new Doctor("طبيب تجريبي");


        public Task<bool> ExistsAsync(
            Guid patientId,
            CancellationToken cancellationToken = default)
        {
            Assert.True(IsInsideTransaction);
            return Task.FromResult(PatientExists);
        }

        public Task<bool> HasOverlapAsync(
     Guid doctorId,
     DateTimeOffset startsAtUtc,
     DateTimeOffset endsAtUtc,
     CancellationToken cancellationToken = default)
        {
            Assert.True(IsInsideTransaction);
            CheckedDoctorId = doctorId;
            return Task.FromResult(HasOverlap);
        }

        public Task AddAsync(
            Appointment appointment,
            CancellationToken cancellationToken = default)
        {
            Assert.True(IsInsideTransaction);
            AddedAppointment = appointment;
            return Task.CompletedTask;
        }

        public Task<int> SaveChangesAsync(
            CancellationToken cancellationToken = default)
        {
            Assert.True(IsInsideTransaction);
            SaveCalls++;

            if (FailOnSave)
            {
                throw new InvalidOperationException(
                    "Simulated save failure.");
            }

            return Task.FromResult(1);
        }
        public Task<Doctor?> GetByIdAsync(
    Guid doctorId,
    CancellationToken cancellationToken = default)
        {
            Assert.True(IsInsideTransaction);
            var doctor = AvailableDoctor;

            if (doctor is null || doctor.Id != doctorId)
            {
                return Task.FromResult<Doctor?>(null);
            }

            return Task.FromResult<Doctor?>(doctor);
        }
        public Guid? LockedDoctorId { get; private set; }

        public bool IsInsideTransaction { get; private set; }

        public bool FailBeforeOperation { get; set; }

        public async Task<T> ExecuteAsync<T>(
            Guid doctorId,
            Func<CancellationToken, Task<T>> operation,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (FailBeforeOperation)
            {
                throw new TimeoutException("Simulated booking lock timeout.");
            }

            LockedDoctorId = doctorId;
            IsInsideTransaction = true;

            try
            {
                return await operation(cancellationToken);
            }
            finally
            {
                IsInsideTransaction = false;
            }
        }
    }
}
