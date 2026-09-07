using Clinic.Application.Abstractions;
using Clinic.Domain.Entities;

namespace Clinic.Application.Appointments;

public sealed class AppointmentBookingService
{
    private readonly IPatientRepository _patients;
    private readonly IAppointmentRepository _appointments;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _timeProvider;

    public AppointmentBookingService(
        IPatientRepository patients,
        IAppointmentRepository appointments,
        IUnitOfWork unitOfWork,
        TimeProvider timeProvider)
    {
        _patients = patients;
        _appointments = appointments;
        _unitOfWork = unitOfWork;
        _timeProvider = timeProvider;
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

        if (request.EndsAt <= request.StartsAt)
        {
            return BookAppointmentResult.Failure(
                BookingError.InvalidTimeRange);
        }

        var startsAtUtc = request.StartsAt.ToUniversalTime();
        var endsAtUtc = request.EndsAt.ToUniversalTime();

        if (startsAtUtc <= _timeProvider.GetUtcNow())
        {
            return BookAppointmentResult.Failure(
                BookingError.StartMustBeInFuture);
        }

        var patientExists = await _patients.ExistsAsync(
            request.PatientId,
            cancellationToken);

        if (!patientExists)
        {
            return BookAppointmentResult.Failure(
                BookingError.PatientNotFound);
        }

        var hasOverlap = await _appointments.HasOverlapAsync(
            startsAtUtc,
            endsAtUtc,
            cancellationToken);

        if (hasOverlap)
        {
            return BookAppointmentResult.Failure(
                BookingError.TimeSlotUnavailable);
        }

        var appointment = new Appointment(
            request.PatientId,
            startsAtUtc,
            endsAtUtc);

        await _appointments.AddAsync(
            appointment,
            cancellationToken);

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return BookAppointmentResult.Success(appointment.Id);
    }
}
