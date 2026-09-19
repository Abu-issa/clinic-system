using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Clinic.Api.Audit;
using Clinic.Infrastructure.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Clinic.Api.Controllers;

public sealed record MobilePasswordBody([Required, StringLength(256)] string Login,
    [Required, StringLength(1024)] string Password);
public sealed record MobileMfaBody([Required, StringLength(8192)] string Challenge,
    [Required, StringLength(100)] string Code);
public sealed record MobileRefreshBody([Required, StringLength(43, MinimumLength = 43)] string RefreshToken);

[ApiController]
[Route("api/mobile/staff/auth")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[EnableRateLimiting("staff-auth")]
public sealed class MobileStaffAuthenticationController(MobileStaffAuthentication auth, HttpAccessAudit audit, TimeProvider clock) : ControllerBase
{
    [HttpPost("login")]
    public async Task<IActionResult> Login(MobilePasswordBody body)
    {
        var result = await auth.LoginAsync(body.Login, body.Password);
        if (result is null) return Failure();
        await audit.SecurityAsync(HttpContext, result.Principal, "staff.mobile.password.accepted");
        if (result.EnrollmentRequired)
            return Problem(statusCode: 403, title: "Complete MFA enrollment using the staff web application.",
                extensions: new Dictionary<string, object?> { ["code"] = "mfa_enrollment_required" });
        return Ok(new { next = "totp", challenge = result.Challenge, expiresIn = 300 });
    }

    [HttpPost("mfa")]
    public async Task<IActionResult> Mfa(MobileMfaBody body)
    {
        var result = await auth.CompleteAsync(body.Challenge, body.Code);
        if (result is null) return Failure();
        await audit.SecurityAsync(HttpContext, result.Principal, "staff.mobile.session.created");
        return Tokens(result);
    }

    [HttpPost("refresh")]
    public async Task<IActionResult> Refresh([FromBody] MobileRefreshBody body)
    {
        var result = await auth.RefreshAsync(body.RefreshToken);
        if (result.ReplayRevocation is not null)
            await audit.SecurityAsync(HttpContext, result.ReplayRevocation, "staff.mobile.replay.revoked");
        if (result.Session is null) return Failure();
        await audit.SecurityAsync(HttpContext, result.Session.Principal, "staff.mobile.session.refreshed");
        return Tokens(result.Session);
    }

    private OkObjectResult Tokens(MobileSessionResult result) => Ok(new
    {
        accessToken = result.AccessToken, tokenType = "Bearer",
        expiresIn = Math.Max(0, (int)Math.Ceiling((result.ExpiresAtUtc - clock.GetUtcNow()).TotalSeconds)),
        expiresAtUtc = result.ExpiresAtUtc, refreshToken = result.RefreshToken,
        refreshExpiresAtUtc = result.RefreshExpiresAtUtc
    });

    [HttpGet("session")]
    [Authorize(Policy = "MobileStaffSession")]
    public IActionResult Session() => Ok(new { staffId = User.FindFirstValue(ClaimTypes.NameIdentifier),
        roles = User.FindAll(ClaimTypes.Role).Select(c => c.Value) });

    [HttpPost("logout")]
    [Authorize(Policy = "MobileStaffSession")]
    public async Task<IActionResult> Logout()
    {
        await auth.RevokeCurrentAsync(User);
        await audit.SecurityAsync(HttpContext, User, "staff.mobile.session.revoked");
        return NoContent();
    }

    private ObjectResult Failure() => Problem(statusCode: 401,
        title: "Unable to authenticate with the supplied credentials.",
        extensions: new Dictionary<string, object?> { ["code"] = "invalid_credentials" });
}
