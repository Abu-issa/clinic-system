using System.Security.Claims;
using Clinic.Api.Appointments;
using Clinic.Application.Appointments;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Clinic.Api.Controllers;

[ApiController]
[Route(
    "api/staff/doctors/{doctorId:guid}/appointments/" +
    "{appointmentId:guid}/cancellation")]
[Authorize(Policy = "StaffBooking")]
public sealed class StaffAppointmentCancellationsController :
    ControllerBase
{
    private readonly AppointmentCancellationService _service;
    private readonly IAuthorizationService _authorization;
    private readonly IAntiforgery _antiforgery;

    public StaffAppointmentCancellationsController(
        AppointmentCancellationService service,
        IAuthorizationService authorization,
        IAntiforgery antiforgery)
    {
        _service = service;
        _authorization = authorization;
        _antiforgery = antiforgery;
    }

    [HttpPost]
    public async Task<IResult> Cancel(
        [FromRoute] Guid doctorId,
        [FromRoute] Guid appointmentId,
        [FromBody] CancelAppointmentBody body,
        CancellationToken cancellationToken)
    {
        var authorizationResult =
            await _authorization.AuthorizeAsync(
                User,
                doctorId,
                "CancelDoctorAppointment");

        if (!authorizationResult.Succeeded)
        {
            return Failure(
                StatusCodes.Status403Forbidden,
                "You cannot cancel appointments for this doctor.",
                "appointment_access_denied");
        }

        var actorUserId = User.FindFirstValue(
            ClaimTypes.NameIdentifier);

        if (string.IsNullOrWhiteSpace(actorUserId) ||
            actorUserId.Length > 200)
        {
            return Failure(
                StatusCodes.Status403Forbidden,
                "A valid staff identity is required.",
                "invalid_staff_identity");
        }

        try
        {
            await _antiforgery.ValidateRequestAsync(HttpContext);
        }
        catch (AntiforgeryValidationException)
        {
            return Failure(
                StatusCodes.Status400BadRequest,
                "The request verification token is invalid.",
                "invalid_csrf_token");
        }

        var result = await _service.CancelAsync(
            new CancelAppointmentRequest(
                appointmentId,
                doctorId,
                body.Reason),
            actorUserId,
            cancellationToken);

        if (result.IsSuccess)
        {
            return Results.NoContent();
        }

        return result.Error switch
        {
            CancellationError.InvalidAppointmentId => Failure(
                400,
                "Appointment ID is invalid.",
                "invalid_appointment_id"),

            CancellationError.InvalidDoctorId => Failure(
                400,
                "Doctor ID is invalid.",
                "invalid_doctor_id"),

            CancellationError.InvalidReason => Failure(
                400,
                "A cancellation reason of up to 500 characters is required.",
                "invalid_cancellation_reason"),

            CancellationError.InvalidActor => Failure(
                403,
                "A valid staff identity is required.",
                "invalid_staff_identity"),

            CancellationError.AppointmentNotFound => Failure(
                404,
                "Appointment was not found.",
                "appointment_not_found"),

            CancellationError.AppointmentCannotBeCancelled => Failure(
                409,
                "The appointment cannot be cancelled in its current state.",
                "appointment_cannot_be_cancelled"),

            _ => throw new InvalidOperationException(
                "Unsupported cancellation error.")
        };
    }

    private IResult Failure(
        int statusCode,
        string title,
        string code)
    {
        return Results.Problem(
            statusCode: statusCode,
            title: title,
            extensions: new Dictionary<string, object?>
            {
                ["code"] = code,
                ["traceId"] = HttpContext.TraceIdentifier
            });
    }
}
