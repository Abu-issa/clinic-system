namespace Clinic.Application.Appointments;

public sealed class BookAppointmentResult
{
    public bool IsSuccess => Error == BookingError.None;

    public Guid? AppointmentId { get; }

    public BookingError Error { get; }

    private BookAppointmentResult(
        Guid? appointmentId,
        BookingError error)
    {
        AppointmentId = appointmentId;
        Error = error;
    }

    public static BookAppointmentResult Success(Guid appointmentId)
    {
        if (appointmentId == Guid.Empty)
        {
            throw new ArgumentException(
                "Appointment ID is required.",
                nameof(appointmentId));
        }

        return new BookAppointmentResult(
            appointmentId,
            BookingError.None);
    }

    public static BookAppointmentResult Failure(BookingError error)
    {
        if (error == BookingError.None)
        {
            throw new ArgumentException(
                "A failure must have an error.",
                nameof(error));
        }

        return new BookAppointmentResult(null, error);
    }
}
