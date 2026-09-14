using Clinic.Application.Abstractions;
using Clinic.Domain.Entities;

namespace Clinic.Application.Appointments;

public sealed class AppointmentBookingService
{
    private readonly IPatientRepository _patients;
    private readonly IDoctorRepository _doctors;
    private readonly IAppointmentRepository _appointments;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IBookingTransaction _bookingTransaction;
    private readonly TimeProvider _timeProvider;
    private readonly IWorkingScheduleRepository _workingSchedule;
    private readonly BookingPolicy _policy;

    public AppointmentBookingService(
        IPatientRepository patients,
        IDoctorRepository doctors,
        IAppointmentRepository appointments,
        IUnitOfWork unitOfWork,
        IBookingTransaction bookingTransaction,
        IWorkingScheduleRepository workingSchedule,
        TimeProvider timeProvider,
        BookingPolicy policy)
    {
        _patients = patients;
        _doctors = doctors;
        _appointments = appointments;
        _unitOfWork = unitOfWork;
        _bookingTransaction = bookingTransaction;
        _workingSchedule = workingSchedule;
        _timeProvider = timeProvider;
        _policy = policy;
    }

    public async Task<BookAppointmentResult> BookAsync(
        BookAppointmentRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.PatientId == Guid.Empty)
        {
            return BookAppointmentResult.Failure(
                BookingError.InvalidPatientId);
        }

        if (request.DoctorId == Guid.Empty)
        {
            return BookAppointmentResult.Failure(
                BookingError.InvalidDoctorId);
        }

        if (!Enum.IsDefined(request.AppointmentType))
        {
            return BookAppointmentResult.Failure(
                BookingError.InvalidAppointmentType);
        }

        var startsAtUtc = request.StartsAt.ToUniversalTime();
        var duration = _policy.Duration(request.AppointmentType);
        if (startsAtUtc > DateTimeOffset.MaxValue - duration)
            return BookAppointmentResult.Failure(BookingError.InvalidTimeRange);
        var endsAtUtc = startsAtUtc + duration;

        if (startsAtUtc <= _timeProvider.GetUtcNow())
        {
            return BookAppointmentResult.Failure(
                BookingError.StartMustBeInFuture);
        }

        return await _bookingTransaction.ExecuteAsync(
            request.DoctorId,
            async token =>
            {
                // Recheck time after waiting for the lock.
                if (startsAtUtc <= _timeProvider.GetUtcNow())
                {
                    return BookAppointmentResult.Failure(
                        BookingError.StartMustBeInFuture);
                }

                var patientExists = await _patients.ExistsAsync(
                    request.PatientId,
                    token);

                if (!patientExists)
                {
                    return BookAppointmentResult.Failure(
                        BookingError.PatientNotFound);
                }

                var doctor = await _doctors.GetByIdAsync(
                    request.DoctorId,
                    token);

                if (doctor is null)
                {
                    return BookAppointmentResult.Failure(
                        BookingError.DoctorNotFound);
                }

                if (!doctor.IsActive)
                {
                    return BookAppointmentResult.Failure(
                        BookingError.DoctorInactive);
                }
                var day = await _workingSchedule.GetDayAsync(
                    request.DoctorId, _policy.LocalDate(startsAtUtc), token);
                var policyError = _policy.ValidateSlot(day, startsAtUtc, endsAtUtc, _timeProvider.GetUtcNow());
                if (policyError != BookingError.None)
                {
                    return BookAppointmentResult.Failure(policyError);
                }

                var hasOverlap = await _appointments.HasOverlapAsync(
                    request.DoctorId,
                    startsAtUtc,
                    endsAtUtc,
                    token);

                if (hasOverlap)
                {
                    return BookAppointmentResult.Failure(
                        BookingError.TimeSlotUnavailable);
                }

                policyError = _policy.ValidateWindow(startsAtUtc, _timeProvider.GetUtcNow());
                if (policyError != BookingError.None) return BookAppointmentResult.Failure(policyError);

                var appointment = new Appointment(
                    request.PatientId,
                    request.DoctorId,
                    startsAtUtc,
                    endsAtUtc,
                    request.AppointmentType);

                await _appointments.AddAsync(appointment, token);

                await _unitOfWork.SaveChangesAsync(token);

                return BookAppointmentResult.Success(appointment.Id);
            },
            cancellationToken);
    }
}
