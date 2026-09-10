namespace Clinic.Application.Schedules;

public enum CloseDoctorDayError
{
    None = 0,
    InvalidDoctorId = 1,
    InvalidDate = 2,
    InvalidReason = 3,
    DateInPast = 4,
    DoctorNotFound = 5,
    AlreadyClosed = 6
}

public sealed class CloseDoctorDayResult
{
    private CloseDoctorDayResult(
        Guid? closureId,
        CloseDoctorDayError error,
        IReadOnlyList<Guid> affectedAppointmentIds)
    {
        ClosureId = closureId;
        Error = error;
        AffectedAppointmentIds = affectedAppointmentIds;
    }

    public Guid? ClosureId { get; }

    public CloseDoctorDayError Error { get; }

    public bool IsSuccess => Error == CloseDoctorDayError.None;

    public IReadOnlyList<Guid> AffectedAppointmentIds { get; }

    public static CloseDoctorDayResult Success(
        Guid closureId,
        IEnumerable<Guid> affectedAppointmentIds)
    {
        if (closureId == Guid.Empty)
        {
            throw new ArgumentException(
                "Closure ID is required.",
                nameof(closureId));
        }

        ArgumentNullException.ThrowIfNull(affectedAppointmentIds);

        return new CloseDoctorDayResult(
            closureId,
            CloseDoctorDayError.None,
            Array.AsReadOnly(affectedAppointmentIds.ToArray()));
    }

    public static CloseDoctorDayResult Failure(
        CloseDoctorDayError error)
    {
        if (error == CloseDoctorDayError.None)
        {
            throw new ArgumentException(
                "Failure requires an error.",
                nameof(error));
        }

        return new CloseDoctorDayResult(
            null,
            error,
            Array.Empty<Guid>());
    }
}
