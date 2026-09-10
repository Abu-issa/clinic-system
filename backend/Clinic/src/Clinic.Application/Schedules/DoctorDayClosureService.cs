using Clinic.Application.Abstractions;
using Clinic.Domain.Entities;

namespace Clinic.Application.Schedules;

public sealed class DoctorDayClosureService
{
    private readonly IDoctorRepository _doctors;
    private readonly IDoctorDayClosureRepository _closures;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IBookingTransaction _transaction;
    private readonly TimeZoneInfo _clinicTimeZone;
    private readonly TimeProvider _timeProvider;

    public DoctorDayClosureService(
        IDoctorRepository doctors,
        IDoctorDayClosureRepository closures,
        IUnitOfWork unitOfWork,
        IBookingTransaction transaction,
        TimeZoneInfo clinicTimeZone,
        TimeProvider timeProvider)
    {
        _doctors = doctors;
        _closures = closures;
        _unitOfWork = unitOfWork;
        _transaction = transaction;
        _clinicTimeZone = clinicTimeZone;
        _timeProvider = timeProvider;
    }

    public async Task<CloseDoctorDayResult> CloseAsync(
        CloseDoctorDayRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (request.DoctorId == Guid.Empty)
        {
            return CloseDoctorDayResult.Failure(
                CloseDoctorDayError.InvalidDoctorId);
        }

        if (request.LocalDate == default ||
            request.LocalDate == DateOnly.MaxValue)
        {
            return CloseDoctorDayResult.Failure(
                CloseDoctorDayError.InvalidDate);
        }

        if (string.IsNullOrWhiteSpace(request.Reason) ||
            request.Reason.Trim().Length > 500)
        {
            return CloseDoctorDayResult.Failure(
                CloseDoctorDayError.InvalidReason);
        }

        return await _transaction.ExecuteAsync(
            request.DoctorId,
            async token =>
            {
                var localNow = TimeZoneInfo.ConvertTime(
                    _timeProvider.GetUtcNow(),
                    _clinicTimeZone);

                var today = DateOnly.FromDateTime(localNow.DateTime);

                if (request.LocalDate < today)
                {
                    return CloseDoctorDayResult.Failure(
                        CloseDoctorDayError.DateInPast);
                }

                var doctor = await _doctors.GetByIdAsync(
                    request.DoctorId,
                    token);

                if (doctor is null)
                {
                    return CloseDoctorDayResult.Failure(
                        CloseDoctorDayError.DoctorNotFound);
                }

                var alreadyClosed = await _closures.ExistsAsync(
                    request.DoctorId,
                    request.LocalDate,
                    token);

                if (alreadyClosed)
                {
                    return CloseDoctorDayResult.Failure(
                        CloseDoctorDayError.AlreadyClosed);
                }

                var localStart = request.LocalDate.ToDateTime(
                    TimeOnly.MinValue,
                    DateTimeKind.Unspecified);

                var localEnd = request.LocalDate.AddDays(1).ToDateTime(
                    TimeOnly.MinValue,
                    DateTimeKind.Unspecified);

                if (_clinicTimeZone.IsInvalidTime(localStart) ||
                    _clinicTimeZone.IsInvalidTime(localEnd) ||
                    _clinicTimeZone.IsAmbiguousTime(localStart) ||
                    _clinicTimeZone.IsAmbiguousTime(localEnd))
                {
                    return CloseDoctorDayResult.Failure(
                        CloseDoctorDayError.InvalidDate);
                }

                var startsAtUtc = new DateTimeOffset(
                    TimeZoneInfo.ConvertTimeToUtc(
                        localStart,
                        _clinicTimeZone));

                var endsAtUtc = new DateTimeOffset(
                    TimeZoneInfo.ConvertTimeToUtc(
                        localEnd,
                        _clinicTimeZone));

                var affectedAppointments =
                    await _closures.GetAffectedAppointmentsAsync(
                        request.DoctorId,
                        startsAtUtc,
                        endsAtUtc,
                        token);

                var closure = new DoctorDayClosure(
                    request.DoctorId,
                    request.LocalDate,
                    request.Reason);

                await _closures.AddAsync(closure, token);
                await _unitOfWork.SaveChangesAsync(token);

                return CloseDoctorDayResult.Success(
                    closure.Id,
                    affectedAppointments.Select(x => x.Id));
            },
            cancellationToken);
    }
    public async Task<DoctorDayClosureDetails?> GetAsync(
    Guid doctorId,
    DateOnly localDate,
    CancellationToken cancellationToken = default)
    {
        if (doctorId == Guid.Empty)
        {
            throw new ArgumentException(
                "Doctor ID is required.",
                nameof(doctorId));
        }

        if (localDate == default || localDate == DateOnly.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(localDate));
        }

        return await _transaction.ExecuteAsync<DoctorDayClosureDetails?>(
            doctorId,
            async token =>
            {
                var closure = await _closures.GetAsync(
                    doctorId,
                    localDate,
                    token);

                if (closure is null)
                {
                    return null;
                }

                var localStart = localDate.ToDateTime(
                    TimeOnly.MinValue,
                    DateTimeKind.Unspecified);

                var localEnd = localDate.AddDays(1).ToDateTime(
                    TimeOnly.MinValue,
                    DateTimeKind.Unspecified);

                var startsAtUtc = new DateTimeOffset(
                    TimeZoneInfo.ConvertTimeToUtc(
                        localStart,
                        _clinicTimeZone));

                var endsAtUtc = new DateTimeOffset(
                    TimeZoneInfo.ConvertTimeToUtc(
                        localEnd,
                        _clinicTimeZone));

                var appointments =
                    await _closures.GetAffectedAppointmentsAsync(
                        doctorId,
                        startsAtUtc,
                        endsAtUtc,
                        token);

                var affectedAppointments = appointments
                    .Select(appointment => new ClosureAffectedAppointment(
                        appointment.Id,
                        appointment.StartsAtUtc,
                        appointment.EndsAtUtc,
                        appointment.Status))
                    .ToArray();

                return new DoctorDayClosureDetails(
                    closure.Id,
                    closure.DoctorId,
                    closure.LocalDate,
                    closure.Reason,
                    Array.AsReadOnly(affectedAppointments));
            },
            cancellationToken);
    }
}
