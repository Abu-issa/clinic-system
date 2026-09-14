using System.Globalization;
using Clinic.Api.Appointments;
using Clinic.Application.Appointments;
using Clinic.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Clinic.Api.Controllers;

[ApiController]
[Route("api/staff/doctors/{doctorId:guid}/availability")]
[Authorize(Policy = "StaffBooking")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class StaffAvailabilityController(
    AppointmentAvailabilityService service,
    IAuthorizationService authorization) : ControllerBase
{
    [HttpGet]
    [EndpointSummary("Read advisory doctor availability for a local date and appointment type")]
    [EndpointDescription("Requires appointments.availability and matching appointment_doctor_id scope. Date is YYYY-MM-DD in Asia/Amman; appointmentType is Consultation or FollowUp. Slots are not reservations. No CSRF token is required for this read.")]
    [ProducesResponseType<AvailabilityDetails>(200)]
    public async Task<IResult> Get(Guid doctorId, [FromQuery] string? date,
        [FromQuery] string? appointmentType, CancellationToken cancellationToken)
    {
        if (!(await authorization.AuthorizeAsync(User, doctorId, "ViewDoctorAvailability")).Succeeded)
            return Results.Problem(statusCode: 403, title: "You cannot view availability for this doctor.",
                extensions: new Dictionary<string, object?>
                {
                    ["code"] = "appointment_access_denied", ["traceId"] = HttpContext.TraceIdentifier
                });
        if (!DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var localDate))
            return BookingHttpResultMapper.MapError(BookingError.InvalidDate, HttpContext);
        if (!Enum.TryParse<AppointmentType>(appointmentType, true, out var type) ||
            !Enum.GetNames<AppointmentType>().Any(name => string.Equals(name, appointmentType, StringComparison.OrdinalIgnoreCase)))
            return BookingHttpResultMapper.MapError(BookingError.InvalidAppointmentType, HttpContext);

        var result = await service.GetAsync(doctorId, localDate, type, cancellationToken);
        return result.Error == BookingError.None ? Results.Ok(result.Details)
            : BookingHttpResultMapper.MapError(result.Error, HttpContext);
    }
}
