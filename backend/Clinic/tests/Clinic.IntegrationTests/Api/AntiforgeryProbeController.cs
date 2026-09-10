using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Clinic.IntegrationTests.Api;

[ApiController]
[Route("__tests/antiforgery")]
[Authorize(Policy = "StaffBooking")]
public sealed class AntiforgeryProbeController : ControllerBase
{
    private readonly IAntiforgery _antiforgery;

    public AntiforgeryProbeController(IAntiforgery antiforgery)
    {
        _antiforgery = antiforgery;
    }

    [HttpGet]
    public IActionResult GetToken()
    {
        var tokens = _antiforgery.GetAndStoreTokens(HttpContext);

        Response.Headers.CacheControl = "no-store";

        return Ok(new
        {
            requestToken = tokens.RequestToken
        });
    }
}
