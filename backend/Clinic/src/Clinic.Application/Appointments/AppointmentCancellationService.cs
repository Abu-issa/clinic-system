using Clinic.Application.Abstractions;
using Clinic.Application.Audit;
using Clinic.Domain.Enums;

namespace Clinic.Application.Appointments;

public sealed class AppointmentCancellationService
{
    private readonly IAppointmentRepository _appointments;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IBookingTransaction _transaction;
    private readonly TimeProvider _timeProvider;
    private readonly IAuditMutationWriter _auditWriter;

    public AppointmentCancellationService(
        IAppointmentRepository appointments,
        IUnitOfWork unitOfWork,
        IBookingTransaction transaction,
        TimeProvider timeProvider,
        IAuditMutationWriter auditWriter)
    {
        _appointments = appointments;
        _unitOfWork = unitOfWork;
        _transaction = transaction;
        _timeProvider = timeProvider;
        _auditWriter = auditWriter;
    }

    public async Task<CancelAppointmentResult> CancelAsync(
        CancelAppointmentRequest request,
        string actorUserId,
        CancellationToken cancellationToken = default)
    {
        using var auditScope = _auditWriter.BeginMutation();
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (request.AppointmentId == Guid.Empty)
        {
            return CancelAppointmentResult.Failure(
                CancellationError.InvalidAppointmentId);
        }

        if (request.DoctorId == Guid.Empty)
        {
            return CancelAppointmentResult.Failure(
                CancellationError.InvalidDoctorId);
        }

        if (string.IsNullOrWhiteSpace(request.Reason) ||
            request.Reason.Trim().Length > 500)
        {
            return CancelAppointmentResult.Failure(
                CancellationError.InvalidReason);
        }

        if (string.IsNullOrWhiteSpace(actorUserId) ||
            actorUserId.Length > 200)
        {
            return CancelAppointmentResult.Failure(
                CancellationError.InvalidActor);
        }

        return await _transaction.ExecuteAsync(
            request.DoctorId,
            async token =>
            {
                var appointment =
                    await _appointments.GetForDoctorAsync(
                        request.AppointmentId,
                        request.DoctorId,
                        token);

                if (appointment is null)
                {
                    return CancelAppointmentResult.Failure(
                        CancellationError.AppointmentNotFound);
                }

                if (appointment.Status != AppointmentStatus.Pending &&
                    appointment.Status != AppointmentStatus.Confirmed)
                {
                    return CancelAppointmentResult.Failure(
                        CancellationError.AppointmentCannotBeCancelled);
                }

                appointment.Cancel(
                    request.Reason,
                    actorUserId,
                    _timeProvider.GetUtcNow());

                // Cancellation free text is never audited.
                _auditWriter.Append(new AuditAppendRequest(actorUserId, "appointment.cancel", "appointment",
                    appointment.Id.ToString("N"), appointment.PatientId, AuditOutcome.Succeeded, null, null));

                await _unitOfWork.SaveChangesAsync(token);

                return CancelAppointmentResult.Success();
            },
            cancellationToken);
    }
}
