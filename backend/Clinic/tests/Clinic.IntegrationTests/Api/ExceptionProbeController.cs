using Microsoft.AspNetCore.Mvc;
using System.Runtime.InteropServices;

namespace Clinic.IntegrationTests.Api;

[ApiController]
[Route("__tests/errors")]
public sealed class ExceptionProbeController : ControllerBase
{
    [HttpGet("unexpected")]
    public IActionResult ThrowUnexpected()
    {
        throw new InvalidOperationException(
            "INTERNAL_TEST_DETAIL_DO_NOT_EXPOSE");
    }
}
