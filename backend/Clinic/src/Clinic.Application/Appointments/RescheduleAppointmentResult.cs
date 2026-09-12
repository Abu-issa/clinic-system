namespace Clinic.Application.Appointments;

public enum ReschedulingError
{
    None = 0,
    InvalidAppointmentId = 1,
    InvalidDoctorId = 2,
    InvalidTimeRange = 3,
    InvalidReason = 4,
    InvalidActor = 5,
    StartMustBeInFuture = 6,
    AppointmentNotFound = 7,
    AppointmentCannotBeRescheduled = 8,
    DoctorNotFound = 9,
    DoctorInactive = 10,
    TimeUnchanged = 11,
    OutsideWorkingHours = 12,
    TimeSlotUnavailable = 13,
        InvalidRowVersion = 14,
    AppointmentChanged = 15
}

public sealed class RescheduleAppointmentResult
{
    private RescheduleAppointmentResult(
        Guid? changeId,
        ReschedulingError error)
    {
        ChangeId = changeId;
        Error = error;
    }

    public Guid? ChangeId { get; }

    public ReschedulingError Error { get; }

    public bool IsSuccess => Error == ReschedulingError.None;

    public static RescheduleAppointmentResult Success(Guid changeId)
    {
        if (changeId == Guid.Empty)
        {
            throw new ArgumentException(
                "Change ID is required.",
                nameof(changeId));
        }

        return new RescheduleAppointmentResult(
            changeId,
            ReschedulingError.None);
    }

    public static RescheduleAppointmentResult Failure(
        ReschedulingError error)
    {
        if (error == ReschedulingError.None)
        {
            throw new ArgumentException(
                "Failure requires an error.",
                nameof(error));
        }

        return new RescheduleAppointmentResult(null, error);
    }
}
