using Clinic.Application.Abstractions;
using Clinic.Domain.Enums;

namespace Clinic.Application.Appointments;

public sealed class AppointmentCancellationService
{
    private readonly IAppointmentRepository _appointments;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IBookingTransaction _transaction;
    private readonly TimeProvider _timeProvider;

    public AppointmentCancellationService(
        IAppointmentRepository appointments,
        IUnitOfWork unitOfWork,
        IBookingTransaction transaction,
        TimeProvider timeProvider)
    {
        _appointments = appointments;
        _unitOfWork = unitOfWork;
        _transaction = transaction;
        _timeProvider = timeProvider;
    }

    public async Task<CancelAppointmentResult> CancelAsync(
        CancelAppointmentRequest request,
        string actorUserId,
        CancellationToken cancellationToken = default)
    {
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

                await _unitOfWork.SaveChangesAsync(token);

                return CancelAppointmentResult.Success();
            },
            cancellationToken);
    }
}
