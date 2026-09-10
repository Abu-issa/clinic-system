using Clinic.Api.Appointments;
using Clinic.Application.Appointments;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Clinic.Api.Controllers;

[ApiController]
[Route("api/staff/appointments")]
[Authorize(Policy = "StaffBooking")]
public sealed class StaffAppointmentsController : ControllerBase
{
    private readonly AppointmentBookingService _bookingService;
    private readonly IAntiforgery _antiforgery;

    public StaffAppointmentsController(
        AppointmentBookingService bookingService,
        IAntiforgery antiforgery)
    {
        _bookingService = bookingService;
        _antiforgery = antiforgery;
    }

    [HttpPost]
    public async Task<IResult> Book(
        [FromBody] BookAppointmentRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            await _antiforgery.ValidateRequestAsync(HttpContext);
        }
        catch (AntiforgeryValidationException)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "The request verification token is invalid.",
                extensions: new Dictionary<string, object?>
                {
                    ["code"] = "invalid_csrf_token",
                    ["traceId"] = HttpContext.TraceIdentifier
                });
        }

        var result = await _bookingService.BookAsync(
            request,
            cancellationToken);

        return BookingHttpResultMapper.Map(result, HttpContext);
    }
}
