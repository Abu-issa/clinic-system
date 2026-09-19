using System.Text.Encodings.Web;
using Clinic.Infrastructure.Authentication;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Clinic.Api.Authentication;

public sealed class MobileStaffHandler(IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger, UrlEncoder encoder, MobileStaffAuthentication auth)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return AuthenticateResult.NoResult();
        var principal = await auth.AuthenticateAsync(header[7..]);
        return principal is null ? AuthenticateResult.Fail("Invalid mobile session.") :
            AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.Headers.WWWAuthenticate = "Bearer";
        Response.Headers.CacheControl = "no-store";
        return Results.Problem(statusCode: 401, title: "A valid mobile staff session is required.")
            .ExecuteAsync(Context);
    }
}
