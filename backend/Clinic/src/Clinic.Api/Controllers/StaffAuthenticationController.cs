using System.ComponentModel.DataAnnotations;
using Clinic.Api.Audit;
using System.Security.Claims;
using Clinic.Application.Abstractions;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.RateLimiting;

namespace Clinic.Api.Controllers;

public sealed record StaffPasswordBody([Required, StringLength(256)] string UserName, [Required, StringLength(1024)] string Password);
public sealed record StaffCodeBody([Required, StringLength(100)] string Code);

[ApiController]
[Route("api/staff/auth")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[EnableRateLimiting("staff-auth")]
public sealed class StaffAuthenticationController(IStaffAuthentication auth, IAntiforgery csrf, HttpAccessAudit audit) : Controller
{
    public override async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        Response.Headers.CacheControl = "no-store";
        // Bind antiforgery to whichever identity currently owns this browser flow.
        if (User.Identity?.IsAuthenticated != true)
        {
            var intermediate = await HttpContext.AuthenticateAsync("ClinicStaffIntermediate");
            if (intermediate.Succeeded) HttpContext.User = intermediate.Principal!;
        }
        if (!HttpMethods.IsGet(Request.Method))
        {
            try { await csrf.ValidateRequestAsync(HttpContext); }
            catch (AntiforgeryValidationException)
            { context.Result = ProblemResult(400, "invalid_csrf_token", "The request verification token is invalid."); return; }
        }
        await next();
    }

    [HttpGet("csrf")]
    public IActionResult Csrf() => Ok(new { requestToken = csrf.GetAndStoreTokens(HttpContext).RequestToken });

    [HttpPost("login")]
    public async Task<IActionResult> Login(StaffPasswordBody body)
    {
        var result = await auth.PasswordAsync(body.UserName, body.Password);
        if (result is null) return Failure();
        await audit.SecurityAsync(HttpContext, result.Principal, "staff.password.accepted");
        await HttpContext.SignOutAsync("ClinicStaff");
        await HttpContext.SignInAsync("ClinicStaffIntermediate", result.Principal, new AuthenticationProperties { IsPersistent = false });
        return Ok(new { next = result.Enrollment ? "enrollment" : "totp", csrfToken = RefreshCsrf(result.Principal) });
    }

    [HttpPost("enrollment/setup")]
    public async Task<IActionResult> Setup()
    {
        var intermediate = await HttpContext.AuthenticateAsync("ClinicStaffIntermediate");
        var setup = intermediate.Succeeded ? await auth.SetupAsync(intermediate.Principal!) : null;
        if (setup is not null) await audit.SecurityAsync(HttpContext, intermediate.Principal!, "staff.enrollment.setup");
        return setup is null ? Failure() : Ok(setup);
    }

    [HttpPost("enrollment/verify")]
    public Task<IActionResult> Enroll(StaffCodeBody body) => Complete(body, false, true);
    [HttpPost("totp")]
    public Task<IActionResult> Totp(StaffCodeBody body) => Complete(body, false, false);
    [HttpPost("recovery")]
    public Task<IActionResult> Recovery(StaffCodeBody body) => Complete(body, true, false);

    private async Task<IActionResult> Complete(StaffCodeBody body, bool recovery, bool enrollment)
    {
        var intermediate = await HttpContext.AuthenticateAsync("ClinicStaffIntermediate");
        var result = intermediate.Succeeded ? await auth.VerifyAsync(intermediate.Principal!, body.Code, recovery, enrollment) : null;
        if (result is null) return Failure();
        await audit.SecurityAsync(HttpContext, result.Principal, enrollment ? "staff.enrollment.completed" :
            recovery ? "staff.recovery.login" : "staff.mfa.completed");
        await HttpContext.SignOutAsync("ClinicStaffIntermediate");
        await HttpContext.SignInAsync("ClinicStaff", result.Principal, new AuthenticationProperties { IsPersistent = false });
        var token = RefreshCsrf(result.Principal);
        return result.RecoveryCodes is null ? Ok(new { csrfToken = token }) : Ok(new { csrfToken = token, recoveryCodes = result.RecoveryCodes });
    }

    [HttpGet("session")]
    [Authorize(Policy = "StaffSession")]
    public IActionResult Session() => Ok(new { staffId = User.FindFirstValue(ClaimTypes.NameIdentifier), roles = User.FindAll(ClaimTypes.Role).Select(x => x.Value) });

    [HttpPost("logout")]
    public async Task<IActionResult> Logout()
    {
        // Revokes all account sessions, also making captured logout cookies unusable.
        if (User.Identity?.IsAuthenticated == true)
        {
            await auth.RevokeAsync(User);
            await audit.SecurityAsync(HttpContext, User, "staff.logout");
        }
        await ClearCookies();
        return Ok(new { csrfToken = RefreshCsrf(new ClaimsPrincipal(new ClaimsIdentity())) });
    }

    [HttpPost("revoke-sessions")]
    [Authorize(Policy = "StaffSession")]
    public async Task<IActionResult> Revoke()
    {
        await auth.RevokeAsync(User);
        await audit.SecurityAsync(HttpContext, User, "staff.sessions-revoke");
        await ClearCookies();
        return Ok(new { csrfToken = RefreshCsrf(new ClaimsPrincipal(new ClaimsIdentity())) });
    }

    private async Task ClearCookies()
    { await HttpContext.SignOutAsync("ClinicStaff"); await HttpContext.SignOutAsync("ClinicStaffIntermediate"); }
    private string? RefreshCsrf(ClaimsPrincipal principal)
    { HttpContext.User = principal; return csrf.GetAndStoreTokens(HttpContext).RequestToken; }
    private ObjectResult Failure() => ProblemResult(401, "invalid_credentials", "Unable to authenticate with the supplied credentials.");
    private ObjectResult ProblemResult(int status, string code, string title) =>
        Problem(statusCode: status, title: title, extensions: new Dictionary<string, object?> { ["code"] = code });
}
