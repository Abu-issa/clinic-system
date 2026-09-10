namespace Clinic.Application.Appointments;

public enum CancellationError
{
    None = 0,
    InvalidAppointmentId = 1,
    InvalidDoctorId = 2,
    InvalidReason = 3,
    InvalidActor = 4,
    AppointmentNotFound = 5,
    AppointmentCannotBeCancelled = 6
}

public sealed class CancelAppointmentResult
{
    private CancelAppointmentResult(CancellationError error)
    {
        Error = error;
    }

    public CancellationError Error { get; }

    public bool IsSuccess => Error == CancellationError.None;

    public static CancelAppointmentResult Success()
    {
        return new CancelAppointmentResult(CancellationError.None);
    }

    public static CancelAppointmentResult Failure(
        CancellationError error)
    {
        if (error == CancellationError.None)
        {
            throw new ArgumentException(
                "Failure requires an error.",
                nameof(error));
        }

        return new CancelAppointmentResult(error);
    }
}
