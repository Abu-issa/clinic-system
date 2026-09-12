using Clinic.Application.Abstractions;
using Clinic.Domain.Enums;
using Clinic.Application.Exceptions;

namespace Clinic.Application.Appointments;

public sealed class AppointmentReschedulingService
{
    private readonly IAppointmentRepository _appointments;
    private readonly IDoctorRepository _doctors;
    private readonly IWorkingScheduleRepository _workingSchedule;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IBookingTransaction _transaction;
    private readonly TimeProvider _timeProvider;

    public AppointmentReschedulingService(
        IAppointmentRepository appointments,
        IDoctorRepository doctors,
        IWorkingScheduleRepository workingSchedule,
        IUnitOfWork unitOfWork,
        IBookingTransaction transaction,
        TimeProvider timeProvider)
    {
        _appointments = appointments;
        _doctors = doctors;
        _workingSchedule = workingSchedule;
        _unitOfWork = unitOfWork;
        _transaction = transaction;
        _timeProvider = timeProvider;
    }

    public async Task<RescheduleAppointmentResult> RescheduleAsync(
        RescheduleAppointmentRequest request,
        string actorUserId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (request.AppointmentId == Guid.Empty)
        {
            return Failure(ReschedulingError.InvalidAppointmentId);
        }

        if (request.DoctorId == Guid.Empty)
        {
            return Failure(ReschedulingError.InvalidDoctorId);
        }

        var startsAtUtc = request.StartsAt.ToUniversalTime();
        var endsAtUtc = request.EndsAt.ToUniversalTime();

        if (endsAtUtc <= startsAtUtc)
        {
            return Failure(ReschedulingError.InvalidTimeRange);
        }

        if (string.IsNullOrWhiteSpace(request.Reason) ||
            request.Reason.Trim().Length > 500)
        {
            return Failure(ReschedulingError.InvalidReason);
        }

        if (string.IsNullOrWhiteSpace(actorUserId) ||
            actorUserId.Length > 200)
        {
            return Failure(ReschedulingError.InvalidActor);
        }

        if (request.ExpectedRowVersion is null ||
            request.ExpectedRowVersion.Length != 8)
        {
            return Failure(ReschedulingError.InvalidRowVersion);
        }

        var expectedRowVersion = request.ExpectedRowVersion.ToArray();

        if (startsAtUtc <= _timeProvider.GetUtcNow())
        {
            return Failure(ReschedulingError.StartMustBeInFuture);
        }

        return await ExecuteWithConcurrencyHandlingAsync(
            request.DoctorId,
            async token =>
            {
                if (startsAtUtc <= _timeProvider.GetUtcNow())
                {
                    return Failure(
                        ReschedulingError.StartMustBeInFuture);
                }

                var appointment =
                    await _appointments.GetForDoctorAsync(
                        request.AppointmentId,
                        request.DoctorId,
                        token);

                if (appointment is null)
                {
                    return Failure(
                        ReschedulingError.AppointmentNotFound);
                }

                if (!appointment.RowVersion.SequenceEqual(
                    expectedRowVersion))
                {
                    return Failure(
                        ReschedulingError.AppointmentChanged);
                }

                if (appointment.Status != AppointmentStatus.Pending &&
                    appointment.Status != AppointmentStatus.Confirmed)
                {
                    return Failure(
                        ReschedulingError.AppointmentCannotBeRescheduled);
                }

                var doctor = await _doctors.GetByIdAsync(
                    request.DoctorId,
                    token);

                if (doctor is null)
                {
                    return Failure(
                        ReschedulingError.DoctorNotFound);
                }

                if (!doctor.IsActive)
                {
                    return Failure(
                        ReschedulingError.DoctorInactive);
                }

                if (appointment.StartsAtUtc == startsAtUtc &&
                    appointment.EndsAtUtc == endsAtUtc)
                {
                    return Failure(
                        ReschedulingError.TimeUnchanged);
                }

                var isWithinWorkingHours =
                    await _workingSchedule.IsWithinActivePeriodAsync(
                        request.DoctorId,
                        startsAtUtc,
                        endsAtUtc,
                        token);

                if (!isWithinWorkingHours)
                {
                    return Failure(
                        ReschedulingError.OutsideWorkingHours);
                }

                var hasOverlap =
                    await _appointments.HasOverlapExcludingAsync(
                        request.DoctorId,
                        appointment.Id,
                        startsAtUtc,
                        endsAtUtc,
                        token);

                if (hasOverlap)
                {
                    return Failure(
                        ReschedulingError.TimeSlotUnavailable);
                }

                var now = _timeProvider.GetUtcNow();

                if (startsAtUtc <= now)
                {
                    return Failure(
                        ReschedulingError.StartMustBeInFuture);
                }

                var change = appointment.Reschedule(
                    startsAtUtc,
                    endsAtUtc,
                    request.Reason,
                    actorUserId,
                    now);

                await _appointments.AddRescheduleAsync(
                    change,
                    token);

                await _unitOfWork.SaveChangesAsync(token);

                return RescheduleAppointmentResult.Success(change.Id);
            },
            cancellationToken);
    }
    private async Task<RescheduleAppointmentResult>
    ExecuteWithConcurrencyHandlingAsync(
        Guid doctorId,
        Func<CancellationToken, Task<RescheduleAppointmentResult>> operation,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _transaction.ExecuteAsync(
                doctorId,
                operation,
                cancellationToken);
        }
        catch (PersistenceConcurrencyException)
        {
            return Failure(ReschedulingError.AppointmentChanged);
        }
    }
    private static RescheduleAppointmentResult Failure(
        ReschedulingError error)
    {
        return RescheduleAppointmentResult.Failure(error);
    }
}
