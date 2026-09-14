using System.Security.Claims;
using Clinic.Api.Appointments;
using Clinic.Application.Appointments;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Clinic.Api.Controllers;

[ApiController]
[Route("api/staff/doctors/{doctorId:guid}/appointments/{appointmentId:guid}/rescheduling")]
[Authorize(Policy = "StaffBooking")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class StaffAppointmentReschedulingController(
    AppointmentReschedulingService service,
    IAuthorizationService authorization,
    IAntiforgery antiforgery) : ControllerBase
{
    [HttpGet]
    public async Task<IResult> Get(
        Guid doctorId, Guid appointmentId, CancellationToken cancellationToken)
    {
        var denied = await CheckAccessAsync(doctorId);
        if (denied is not null)
        {
            return denied;
        }

        var details = await service.GetAsync(appointmentId, doctorId, cancellationToken);
        return details is null
            ? Failure(404, "Appointment was not found.", "appointment_not_found")
            : Results.Ok(details);
    }

    [HttpPost]
    public async Task<IResult> Reschedule(
        Guid doctorId,
        Guid appointmentId,
        [FromBody] RescheduleAppointmentBody body,
        CancellationToken cancellationToken)
    {
        var denied = await CheckAccessAsync(doctorId);
        if (denied is not null)
        {
            return denied;
        }

        try
        {
            await antiforgery.ValidateRequestAsync(HttpContext);
        }
        catch (AntiforgeryValidationException)
        {
            return Failure(400, "The request verification token is invalid.", "invalid_csrf_token");
        }

        var result = await service.RescheduleAsync(
            new RescheduleAppointmentRequest(appointmentId, doctorId,
                body.StartsAt, body.EndsAt, body.Reason, body.ExpectedRowVersion),
            User.FindFirstValue(ClaimTypes.NameIdentifier)!, cancellationToken);

        if (result.IsSuccess)
        {
            return Results.Ok(new RescheduleAppointmentResponse(
                result.ChangeId!.Value, result.RowVersion!));
        }

        var (status, title, code) = result.Error switch
        {
            ReschedulingError.InvalidAppointmentId => (400, "Appointment ID is invalid.", "invalid_appointment_id"),
            ReschedulingError.InvalidDoctorId => (400, "Doctor ID is invalid.", "invalid_doctor_id"),
            ReschedulingError.InvalidTimeRange => (400, "Appointment end must be after its start.", "invalid_time_range"),
            ReschedulingError.InvalidReason => (400, "A rescheduling reason of up to 500 characters is required.", "invalid_rescheduling_reason"),
            ReschedulingError.InvalidActor => (403, "A valid staff identity is required.", "invalid_staff_identity"),
            ReschedulingError.InvalidRowVersion => (400, "An eight-byte row version is required.", "invalid_row_version"),
            ReschedulingError.StartMustBeInFuture => (400, "The new appointment start must be in the future.", "start_must_be_in_future"),
            ReschedulingError.AppointmentNotFound => (404, "Appointment was not found.", "appointment_not_found"),
            ReschedulingError.DoctorNotFound => (404, "Doctor was not found.", "doctor_not_found"),
            ReschedulingError.AppointmentCannotBeRescheduled => (409, "The appointment cannot be rescheduled in its current state.", "appointment_cannot_be_rescheduled"),
            ReschedulingError.DoctorInactive => (409, "The doctor is not accepting bookings.", "doctor_inactive"),
            ReschedulingError.TimeUnchanged => (409, "The new appointment time must differ from the current time.", "appointment_time_unchanged"),
            ReschedulingError.OutsideWorkingHours => (409, "The selected time is outside the doctor's working hours.", "outside_working_hours"),
            ReschedulingError.TimeSlotUnavailable => (409, "The selected appointment time is unavailable.", "time_slot_unavailable"),
            ReschedulingError.AppointmentChanged => (409, "The appointment changed. Reload it before rescheduling.", "appointment_changed"),
            _ => throw new InvalidOperationException("Unsupported rescheduling error.")
        };
        return Failure(status, title, code);
    }

    private async Task<IResult?> CheckAccessAsync(Guid doctorId)
    {
        var result = await authorization.AuthorizeAsync(User, doctorId, "RescheduleDoctorAppointment");
        if (!result.Succeeded)
        {
            return Failure(403, "You cannot reschedule appointments for this doctor.", "appointment_access_denied");
        }

        var actor = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return string.IsNullOrWhiteSpace(actor) || actor.Length > 200
            ? Failure(403, "A valid staff identity is required.", "invalid_staff_identity")
            : null;
    }

    private IResult Failure(int statusCode, string title, string code) =>
        Results.Problem(statusCode: statusCode, title: title,
            extensions: new Dictionary<string, object?>
            {
                ["code"] = code,
                ["traceId"] = HttpContext.TraceIdentifier
            });
}
