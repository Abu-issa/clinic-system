using System.Security.Claims;

namespace Clinic.Application.Abstractions;

public sealed record StaffAuthResult(ClaimsPrincipal Principal, bool Enrollment, string[]? RecoveryCodes = null);
public sealed record AuthenticatorSetup(string SharedKey, string AuthenticatorUri);

public interface IStaffAuthentication
{
    Task<StaffAuthResult?> PasswordAsync(string userName, string password);
    Task<AuthenticatorSetup?> SetupAsync(ClaimsPrincipal intermediate);
    Task<StaffAuthResult?> VerifyAsync(ClaimsPrincipal intermediate, string code, bool recovery, bool enrollment);
    Task<bool> ValidateAsync(ClaimsPrincipal principal, bool intermediate);
    Task RevokeAsync(ClaimsPrincipal principal);
}
