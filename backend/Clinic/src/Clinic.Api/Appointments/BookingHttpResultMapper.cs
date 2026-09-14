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

        return MapError(result.Error, httpContext);
    }

    public static IResult MapError(BookingError error, HttpContext httpContext)
    {
        var (statusCode, code, title) = error switch
        {
            BookingError.InvalidAppointmentType => (400, "invalid_appointment_type", "Choose Consultation or FollowUp."),
            BookingError.InvalidDate => (400, "invalid_date", "A valid supported local date is required."),
            BookingError.OutsideBookingWindow => (400, "outside_booking_window", "The date is outside the booking horizon."),
            BookingError.InsufficientNotice => (400, "insufficient_notice", "The start does not meet minimum advance notice."),
            BookingError.OffGrid => (400, "off_grid", "The start must align with a working period's slot interval."),
            BookingError.InvalidLocalTime => (400, "invalid_local_time", "Ambiguous or invalid local appointment times are unsupported."),
            BookingError.AppointmentNotFound => (404, "appointment_not_found", "Appointment was not found."),
            BookingError.AppointmentCannotBeRescheduled => (409, "appointment_cannot_be_rescheduled", "The appointment cannot be rescheduled in its current state."),
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
