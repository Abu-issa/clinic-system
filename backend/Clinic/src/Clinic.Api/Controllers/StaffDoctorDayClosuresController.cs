using Clinic.Api.Schedules;
using Clinic.Application.Schedules;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Clinic.Api.Controllers;

[ApiController]
[Route("api/staff/doctors/{doctorId:guid}/day-closures")]
[Authorize(Policy = "StaffScheduleManagement")]
public sealed class StaffDoctorDayClosuresController : ControllerBase
{
    private readonly DoctorDayClosureService _service;
    private readonly IAuthorizationService _authorization;
    private readonly IAntiforgery _antiforgery;

    public StaffDoctorDayClosuresController(
        DoctorDayClosureService service,
        IAuthorizationService authorization,
        IAntiforgery antiforgery)
    {
        _service = service;
        _authorization = authorization;
        _antiforgery = antiforgery;
    }

    [HttpPost]
    public async Task<IResult> Close(
        [FromRoute] Guid doctorId,
        [FromBody] CloseDoctorDayBody body,
        CancellationToken cancellationToken)
    {
        var authorizationResult = await _authorization.AuthorizeAsync(
            User,
            doctorId,
            "ManageDoctorSchedule");

        if (!authorizationResult.Succeeded)
        {
            return Failure(
                StatusCodes.Status403Forbidden,
                "You cannot manage this doctor's schedule.",
                "schedule_access_denied");
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

        var result = await _service.CloseAsync(
            new CloseDoctorDayRequest(
                doctorId,
                body.LocalDate,
                body.Reason),
            cancellationToken);

        if (result.IsSuccess)
        {
            if (result.ClosureId is not Guid closureId ||
                closureId == Guid.Empty)
            {
                throw new InvalidOperationException(
                    "Successful closure result must contain an ID.");
            }

            return Results.Json(
                new
                {
                    closureId,
                    affectedAppointmentIds =
                        result.AffectedAppointmentIds
                },
                statusCode: StatusCodes.Status201Created);
        }

        return result.Error switch
        {
            CloseDoctorDayError.InvalidDoctorId => Failure(
                400,
                "Doctor ID is invalid.",
                "invalid_doctor_id"),

            CloseDoctorDayError.InvalidDate => Failure(
                400,
                "Closure date is invalid or unsupported.",
                "invalid_closure_date"),

            CloseDoctorDayError.InvalidReason => Failure(
                400,
                "A reason of up to 500 characters is required.",
                "invalid_closure_reason"),

            CloseDoctorDayError.DateInPast => Failure(
                400,
                "Closure date cannot be in the past.",
                "closure_date_in_past"),

            CloseDoctorDayError.DoctorNotFound => Failure(
                404,
                "Doctor was not found.",
                "doctor_not_found"),

            CloseDoctorDayError.AlreadyClosed => Failure(
                409,
                "This day is already closed for the doctor.",
                "doctor_day_already_closed"),

            _ => throw new InvalidOperationException(
                "Unsupported closure error.")
        };
    }
    [HttpGet]
    [ResponseCache(
    NoStore = true,
    Location = ResponseCacheLocation.None)]
    public async Task<IResult> Get(
    [FromRoute] Guid doctorId,
    [FromQuery] DateOnly? localDate,
    CancellationToken cancellationToken)
    {
        var authorizationResult = await _authorization.AuthorizeAsync(
            User,
            doctorId,
            "ManageDoctorSchedule");

        if (!authorizationResult.Succeeded)
        {
            return Failure(
                StatusCodes.Status403Forbidden,
                "You cannot manage this doctor's schedule.",
                "schedule_access_denied");
        }

        if (localDate is not DateOnly date ||
            date == default ||
            date == DateOnly.MaxValue)
        {
            return Failure(
                StatusCodes.Status400BadRequest,
                "A valid closure date is required.",
                "invalid_closure_date");
        }

        var details = await _service.GetAsync(
            doctorId,
            date,
            cancellationToken);

        if (details is null)
        {
            return Failure(
                StatusCodes.Status404NotFound,
                "No closure was found for this doctor and date.",
                "doctor_day_closure_not_found");
        }

        return Results.Ok(details);
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
