using Clinic.Application.Appointments;

namespace Clinic.Api.Appointments;

public static class BookingHttpResultMapper
{
    public static IResult Map(
        BookAppointmentResult result,
        HttpContext httpContext)
    {
        if (result.IsSuccess)
        {
            if (result.AppointmentId is not Guid appointmentId ||
                appointmentId == Guid.Empty)
            {
                throw new InvalidOperationException(
                    "A successful booking must have an appointment ID.");
            }

            return Results.Json(
                new BookAppointmentResponse(appointmentId),
                statusCode: StatusCodes.Status201Created);
        }

        var (statusCode, code, title) = result.Error switch
        {
            BookingError.InvalidPatientId =>
                (
                    StatusCodes.Status400BadRequest,
                    "invalid_patient_id",
                    "A valid patient ID is required."
                ),

            BookingError.InvalidDoctorId =>
                (
                    StatusCodes.Status400BadRequest,
                    "invalid_doctor_id",
                    "A valid doctor ID is required."
                ),

            BookingError.InvalidTimeRange =>
                (
                    StatusCodes.Status400BadRequest,
                    "invalid_time_range",
                    "Appointment end must be after its start."
                ),

            BookingError.StartMustBeInFuture =>
                (
                    StatusCodes.Status400BadRequest,
                    "start_must_be_in_future",
                    "Appointment start must be in the future."
                ),

            BookingError.PatientNotFound =>
                (
                    StatusCodes.Status404NotFound,
                    "patient_not_found",
                    "The patient was not found."
                ),

            BookingError.DoctorNotFound =>
                (
                    StatusCodes.Status404NotFound,
                    "doctor_not_found",
                    "The doctor was not found."
                ),

            BookingError.DoctorInactive =>
                (
                    StatusCodes.Status409Conflict,
                    "doctor_inactive",
                    "The doctor is not accepting new bookings."
                ),
            BookingError.OutsideWorkingHours =>
(
    StatusCodes.Status409Conflict,
    "outside_working_hours",
    "The selected time is outside the doctor's working hours."
),

            BookingError.TimeSlotUnavailable =>
                (
                    StatusCodes.Status409Conflict,
                    "time_slot_unavailable",
                    "The selected appointment time is unavailable."
                ),

            _ => throw new InvalidOperationException(
                "The booking error has no HTTP mapping.")
        };

        return Results.Problem(
            statusCode: statusCode,
            title: title,
            extensions: new Dictionary<string, object?>
            {
                ["code"] = code,
                ["traceId"] = httpContext.TraceIdentifier
            });
    }
}
