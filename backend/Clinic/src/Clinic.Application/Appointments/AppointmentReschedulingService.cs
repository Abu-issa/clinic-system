using Clinic.Application.Abstractions;
using Clinic.Application.Audit;
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
    private readonly BookingPolicy _policy;
    private readonly IAuditMutationWriter _auditWriter;

    public AppointmentReschedulingService(
        IAppointmentRepository appointments,
        IDoctorRepository doctors,
        IWorkingScheduleRepository workingSchedule,
        IUnitOfWork unitOfWork,
        IBookingTransaction transaction,
        TimeProvider timeProvider,
        BookingPolicy policy,
        IAuditMutationWriter auditWriter)
    {
        _appointments = appointments;
        _doctors = doctors;
        _workingSchedule = workingSchedule;
        _unitOfWork = unitOfWork;
        _transaction = transaction;
        _timeProvider = timeProvider;
        _policy = policy;
        _auditWriter = auditWriter;
    }

    public async Task<RescheduleAppointmentResult> RescheduleAsync(
        RescheduleAppointmentRequest request,
        string actorUserId,
        CancellationToken cancellationToken = default)
    {
        using var auditScope = _auditWriter.BeginMutation();
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

                if (endsAtUtc - startsAtUtc != appointment.EndsAtUtc - appointment.StartsAtUtc)
                    return Failure(ReschedulingError.DurationChanged);

                var day = await _workingSchedule.GetDayAsync(
                    request.DoctorId, _policy.LocalDate(startsAtUtc), token);
                var policyError = _policy.ValidateSlot(day, startsAtUtc, endsAtUtc, _timeProvider.GetUtcNow());
                if (policyError != BookingError.None)
                {
                    return Failure(MapPolicyError(policyError));
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

                policyError = _policy.ValidateWindow(startsAtUtc, now);
                if (policyError != BookingError.None)
                {
                    return Failure(MapPolicyError(policyError));
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

                // Reschedule reason free text is never audited; only the opaque change identifier.
                _auditWriter.Append(new AuditAppendRequest(actorUserId, "appointment.reschedule", "appointment",
                    appointment.Id.ToString("N"), appointment.PatientId, AuditOutcome.Succeeded, null,
                    [new KeyValuePair<string, string>("reschedule.id", change.Id.ToString("N"))]));

                await _unitOfWork.SaveChangesAsync(token);

                return RescheduleAppointmentResult.Success(change.Id, appointment.RowVersion);
            },
            cancellationToken);
    }
    public async Task<AppointmentReschedulingDetails?> GetAsync(
        Guid appointmentId,
        Guid doctorId,
        CancellationToken cancellationToken = default)
    {
        var appointment = await _appointments.GetForDoctorAsync(
            appointmentId, doctorId, cancellationToken);

        return appointment is null ? null : new AppointmentReschedulingDetails(
            appointment.Id, appointment.DoctorId, appointment.StartsAtUtc,
            appointment.EndsAtUtc, appointment.Status, appointment.RowVersion.ToArray(), appointment.Type, appointment.PatientId);
    }

    private static ReschedulingError MapPolicyError(BookingError error) => error switch
    {
        BookingError.StartMustBeInFuture => ReschedulingError.StartMustBeInFuture,
        BookingError.OutsideWorkingHours => ReschedulingError.OutsideWorkingHours,
        BookingError.OffGrid => ReschedulingError.OffGrid,
        BookingError.InsufficientNotice => ReschedulingError.InsufficientNotice,
        BookingError.OutsideBookingWindow or BookingError.InvalidDate => ReschedulingError.OutsideBookingWindow,
        BookingError.InvalidLocalTime => ReschedulingError.InvalidLocalTime,
        BookingError.InvalidTimeRange => ReschedulingError.InvalidTimeRange,
        _ => throw new InvalidOperationException("Unsupported booking policy error.")
    };

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
