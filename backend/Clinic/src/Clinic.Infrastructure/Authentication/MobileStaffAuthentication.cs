using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Clinic.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;

namespace Clinic.Infrastructure.Authentication;

public sealed record MobileLoginResult(ClaimsPrincipal Principal, bool EnrollmentRequired, string? Challenge);
public sealed record MobileSessionResult(ClaimsPrincipal Principal, string AccessToken, DateTimeOffset ExpiresAtUtc,
    string RefreshToken, DateTimeOffset RefreshExpiresAtUtc);
public sealed record MobileRefreshResult(MobileSessionResult? Session, ClaimsPrincipal? ReplayRevocation = null);

public sealed class MobileStaffAuthentication(ClinicDbContext db, StaffAuthentication auth,
    IDataProtectionProvider protection, TimeProvider clock)
{
    public const string Scheme = "ClinicStaffMobile";
    public const string SessionClaim = "mobile_session_id";
    private readonly TicketDataFormat challenges = new(protection.CreateProtector("Clinic.Mobile.Challenge.v1"));
    private readonly TicketDataFormat sessions = new(protection.CreateProtector("Clinic.Mobile.Session.v1"));

    public async Task<MobileLoginResult?> LoginAsync(string login, string password)
    {
        var result = await auth.MobilePasswordAsync(login, password);
        if (result is null) return null;
        // Enrollment stays in the existing web flow. Password alone never issues a session.
        return new(result.Principal, result.Enrollment, result.Enrollment ? null : challenges.Protect(
            new AuthenticationTicket(result.Principal, new AuthenticationProperties
            { ExpiresUtc = clock.GetUtcNow().AddMinutes(5) }, Scheme)));
    }

    public async Task<MobileSessionResult?> CompleteAsync(string challenge, string code)
    {
        if (challenge.Length > 8192 || code.Length > 100) return null;
        var ticket = challenges.Unprotect(challenge);
        if (ticket?.Properties.ExpiresUtc is not { } expiry || expiry <= clock.GetUtcNow()) return null;
        // Existing SQL application lock makes challenge consumption single use across processes.
        var result = await auth.VerifyAsync(ticket.Principal, code, recovery: false, enrollment: false);
        if (result is null) return null;
        var id = result.Principal.FindFirstValue(StaffAuthentication.UserIdClaim)!;
        await using var transaction = await db.Database.BeginTransactionAsync();
        await StaffAuthentication.LockAsync(db, id);
        // Cover revocation between MFA completion and session creation.
        if (!await auth.ValidateAsync(result.Principal, false)) return null;
        var sessionId = Guid.NewGuid();
        var principal = new ClaimsPrincipal(new ClaimsIdentity(result.Principal.Claims.Where(c =>
            c.Type is ClaimTypes.NameIdentifier or ClaimTypes.Role or StaffAuthentication.UserIdClaim
                or StaffAuthentication.StampClaim or "amr"), Scheme));
        ((ClaimsIdentity)principal.Identity!).AddClaim(new(SessionClaim, sessionId.ToString()));
        var token = NewToken();
        var refreshToken = NewToken();
        var now = clock.GetUtcNow();
        var session = new MobileStaffSession
        {
            Id = sessionId, StaffUserId = id, TokenHash = Hash(token), CreatedAtUtc = now,
            ExpiresAtUtc = now.AddMinutes(15),
            RefreshExpiresAtUtc = now.AddDays(7),
            ProtectedPrincipal = sessions.Protect(new AuthenticationTicket(principal, Scheme))
        };
        db.Add(session);
        db.Add(new MobileStaffRefreshToken { TokenHash = Hash(refreshToken), SessionId = sessionId, CreatedAtUtc = now });
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return new(principal, token, session.ExpiresAtUtc, refreshToken, session.RefreshExpiresAtUtc.Value);
    }

    public async Task<MobileRefreshResult> RefreshAsync(string refreshToken)
    {
        if (!IsToken(refreshToken)) return new(null);
        var digest = Hash(refreshToken);
        // Lookup only identifies the lock owner. All mutable state is re-read after acquiring it.
        var owner = await (from token in db.Set<MobileStaffRefreshToken>().AsNoTracking()
            join session in db.Set<MobileStaffSession>().AsNoTracking() on token.SessionId equals session.Id
            where token.TokenHash == digest
            select new { session.Id, session.StaffUserId }).SingleOrDefaultAsync();
        if (owner is null) return new(null);
        await using var transaction = await db.Database.BeginTransactionAsync();
        await StaffAuthentication.LockAsync(db, owner.StaffUserId);
        var current = await db.Set<MobileStaffSession>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == owner.Id);
        var presented = await db.Set<MobileStaffRefreshToken>().AsNoTracking().SingleOrDefaultAsync(x => x.TokenHash == digest);
        if (current is null || presented is null || current.RevokedAtUtc is not null) return new(null);
        var now = clock.GetUtcNow();
        if (presented.ConsumedAtUtc is not null)
        {
            // No grace period: a duplicate request is indistinguishable from credential theft.
            // Revoking the shared session also invalidates the winner of a concurrent refresh.
            await db.Set<MobileStaffSession>().Where(x => x.Id == current.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.RevokedAtUtc, now));
            await transaction.CommitAsync();
            return new(null, new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(StaffAuthentication.UserIdClaim, current.StaffUserId)])));
        }
        if (current.RefreshExpiresAtUtc is not { } refreshExpiry || refreshExpiry <= now) return new(null);
        var principal = await ValidPrincipalAsync(current);
        if (principal is null) return new(null);
        now = clock.GetUtcNow();
        if (refreshExpiry <= now) return new(null);
        // The family deadline is immutable; even access must not outlive it.
        var accessExpiry = now.AddMinutes(15) < refreshExpiry ? now.AddMinutes(15) : refreshExpiry;
        var access = NewToken();
        var replacement = NewToken();
        await db.Set<MobileStaffRefreshToken>().Where(x => x.TokenHash == digest)
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.ConsumedAtUtc, now));
        await db.Set<MobileStaffSession>().Where(x => x.Id == current.Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.TokenHash, Hash(access))
                .SetProperty(x => x.ExpiresAtUtc, accessExpiry));
        db.Add(new MobileStaffRefreshToken { TokenHash = Hash(replacement), SessionId = current.Id, CreatedAtUtc = now });
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return new(new(principal, access, accessExpiry, replacement, refreshExpiry));
    }

    public async Task<ClaimsPrincipal?> AuthenticateAsync(string token)
    {
        if (!IsToken(token)) return null;
        var digest = Hash(token);
        var session = await db.Set<MobileStaffSession>().AsNoTracking().SingleOrDefaultAsync(x =>
            x.TokenHash == digest && x.RevokedAtUtc == null && x.ExpiresAtUtc > clock.GetUtcNow());
        if (session is null) return null;
        return await ValidPrincipalAsync(session);
    }

    private async Task<ClaimsPrincipal?> ValidPrincipalAsync(MobileStaffSession session)
    {
        var ticket = sessions.Unprotect(session.ProtectedPrincipal);
        if (ticket is null || ticket.Principal.FindFirstValue(StaffAuthentication.UserIdClaim) != session.StaffUserId ||
            ticket.Principal.FindFirstValue(SessionClaim) != session.Id.ToString() ||
            !await auth.ValidateAsync(ticket.Principal, false)) return null;
        return ticket.Principal;
    }

    public async Task RevokeCurrentAsync(ClaimsPrincipal principal)
    {
        var id = Guid.Parse(principal.FindFirstValue(SessionClaim)!);
        var userId = principal.FindFirstValue(StaffAuthentication.UserIdClaim)!;
        await using var transaction = await db.Database.BeginTransactionAsync();
        await StaffAuthentication.LockAsync(db, userId);
        await db.Set<MobileStaffSession>().Where(x => x.Id == id && x.StaffUserId == userId && x.RevokedAtUtc == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.RevokedAtUtc, clock.GetUtcNow()));
        await transaction.CommitAsync();
    }

    private static bool IsToken(string token) => token.Length == 43 &&
        token.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
    private static string NewToken() => WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(token)));
}
