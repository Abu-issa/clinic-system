using System.Security.Claims;
using Clinic.Application.Abstractions;
using Clinic.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Clinic.Infrastructure.Authentication;

public sealed class StaffAuthentication(ClinicDbContext db, UserManager<StaffUser> users,
    TimeProvider clock) : IStaffAuthentication
{
    public const string UserIdClaim = "staff_id";
    public const string StampClaim = "staff_stamp";
    public const string ChallengeClaim = "staff_challenge";
    public const string EnrollmentClaim = "staff_enrollment";
    public static readonly string[] Roles = ["Doctor", "Receptionist", "DoctorAssistant"];
    public static readonly string[] InitialPermissions = ["appointments.availability", "appointments.reschedule", "appointments.cancel", "schedule.manage"];
    public static readonly string[] Permissions = [.. InitialPermissions, "patients.admin.read", "patients.admin.write", "patients.clinical.read", "patients.clinical.write", "visits.read", "visits.write", "visits.finalize", "visits.amend", "vitals.write", "medications.read", "medications.manage", "prescriptions.read", "prescriptions.write", "prescriptions.finalize", "prescriptions.release", "prescriptions.cancel"];
    private static readonly StaffUser DummyUser = new();
    private static readonly PasswordHasher<StaffUser> DummyHasher = new();
    private static readonly string DummyHash = DummyHasher.HashPassword(DummyUser, Guid.NewGuid().ToString());

    // All credential mutations, challenge consumption and administrative changes share this lock.
    // Locks are database-scoped and transaction-owned, including across API processes.
    internal static async Task LockAsync(ClinicDbContext db, string id)
    {
        var resource = "Clinic.Staff:" + id;
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            DECLARE @result int;
            EXEC @result = sys.sp_getapplock @Resource={resource}, @LockMode='Exclusive',
                @LockOwner='Transaction', @LockTimeout=10000;
            IF @result < 0 THROW 51000, 'Staff operation lock unavailable.', 1;
            """);
    }

    public async Task<StaffAuthResult?> PasswordAsync(string userName, string password)
    {
        if (userName.Length > 256 || password.Length > 1024) return null;
        var normalized = users.NormalizeName(userName);
        var id = await db.Users.AsNoTracking().Where(x => x.NormalizedUserName == normalized)
            .Select(x => x.Id).SingleOrDefaultAsync();
        if (id is null)
        {
            DummyHasher.VerifyHashedPassword(DummyUser, DummyHash, password);
            return null;
        }
        await using var transaction = await db.Database.BeginTransactionAsync();
        await LockAsync(db, id);
        var user = (await users.FindByIdAsync(id))!;
        if (!await AllowedAsync(user) || await users.IsLockedOutAsync(user))
        {
            DummyHasher.VerifyHashedPassword(DummyUser, DummyHash, password);
            return null;
        }
        if (!await users.CheckPasswordAsync(user, password))
        {
            await FailedAsync(user);
            await transaction.CommitAsync();
            return null;
        }
        // Do not reset failed attempts after password alone: otherwise TOTP lockout is bypassable.
        if (!user.TwoFactorEnabled && string.IsNullOrEmpty(await users.GetAuthenticatorKeyAsync(user)))
            Require(await users.ResetAuthenticatorKeyAsync(user));
        user.ChallengeId = Guid.NewGuid().ToString("N");
        user.ChallengeExpiresAt = clock.GetUtcNow().AddMinutes(5);
        Require(await users.UpdateAsync(user));
        var principal = Intermediate(user);
        await transaction.CommitAsync();
        return new(principal, !user.TwoFactorEnabled);
    }

    private async Task<bool> AllowedAsync(StaffUser user) => user.IsEnabled &&
        (await users.GetRolesAsync(user)).Any(Roles.Contains);

    private bool Matches(StaffUser user, ClaimsPrincipal principal, bool intermediate) =>
        principal.FindFirstValue(StampClaim) == user.SecurityStamp &&
        (!intermediate || (user.ChallengeId is not null &&
            principal.FindFirstValue(ChallengeClaim) == user.ChallengeId &&
            user.ChallengeExpiresAt > clock.GetUtcNow() &&
            principal.HasClaim(EnrollmentClaim, "true") == !user.TwoFactorEnabled));

    public async Task<bool> ValidateAsync(ClaimsPrincipal principal, bool intermediate)
    {
        var id = principal.FindFirstValue(UserIdClaim);
        if (id is null) return false;
        // Always read persisted state, including when this context previously loaded the user.
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id);
        if (user is null || !Matches(user, principal, intermediate) || !await AllowedAsync(user) ||
            await users.IsLockedOutAsync(user) || !intermediate && !user.TwoFactorEnabled) return false;
        if (!intermediate)
        {
            // Every full-session permission and scope claim was issued from persisted state, so
            // revalidation selects them by claim type: grant removal is always detected without
            // relying on per-feature value prefixes. A stamp check alone does not detect Identity
            // claim removal without stamp rotation; additions require a new login.
            var recordClaims = principal.Claims.Where(c =>
                c.Type is "patient_record_id" or "appointment_doctor_id" or "schedule_doctor_id" ||
                c.Type == "permission").ToArray();
            if (recordClaims.Length > 0)
            {
                var persisted = await users.GetClaimsAsync(user);
                var roles = await users.GetRolesAsync(user);
                if (recordClaims.Any(c => !persisted.Any(p => p.Type == c.Type && p.Value == c.Value)) ||
                    principal.FindAll(ClaimTypes.Role).Any(c => !roles.Contains(c.Value))) return false;
            }
        }
        return true;
    }

    public async Task<AuthenticatorSetup?> SetupAsync(ClaimsPrincipal intermediate)
    {
        var id = intermediate.FindFirstValue(UserIdClaim);
        if (id is null || !intermediate.HasClaim(EnrollmentClaim, "true")) return null;
        await using var transaction = await db.Database.BeginTransactionAsync();
        await LockAsync(db, id);
        var user = await users.FindByIdAsync(id);
        if (user is null || !Matches(user, intermediate, true) || !await AllowedAsync(user) || await users.IsLockedOutAsync(user)) return null;
        var key = await users.GetAuthenticatorKeyAsync(user);
        if (key is null) return null;
        await transaction.CommitAsync();
        return new(key, $"otpauth://totp/Clinic:{Uri.EscapeDataString(user.UserName!)}?secret={key}&issuer=Clinic&digits=6");
    }

    public async Task<StaffAuthResult?> VerifyAsync(ClaimsPrincipal intermediate, string code, bool recovery, bool enrollment)
    {
        var id = intermediate.FindFirstValue(UserIdClaim);
        if (id is null || code.Length > 100 || recovery && enrollment) return null;
        await using var transaction = await db.Database.BeginTransactionAsync();
        await LockAsync(db, id);
        var user = await users.FindByIdAsync(id);
        if (user is null || !Matches(user, intermediate, true) || !await AllowedAsync(user) ||
            await users.IsLockedOutAsync(user) || enrollment == user.TwoFactorEnabled) return null;
        var valid = recovery
            ? (await users.RedeemTwoFactorRecoveryCodeAsync(user, code.Trim())).Succeeded
            : await users.VerifyTwoFactorTokenAsync(user, users.Options.Tokens.AuthenticatorTokenProvider,
                code.Replace(" ", "").Replace("-", ""));
        if (!valid)
        {
            await FailedAsync(user);
            await transaction.CommitAsync();
            return null;
        }
        string[]? codes = null;
        if (enrollment)
        {
            Require(await users.SetTwoFactorEnabledAsync(user, true));
            codes = (await users.GenerateNewTwoFactorRecoveryCodesAsync(user, 10))!.ToArray();
        }
        Require(await users.ResetAccessFailedCountAsync(user));
        user.ChallengeId = null;
        user.ChallengeExpiresAt = null;
        Require(await users.UpdateAsync(user));
        var principal = await FullAsync(user);
        await transaction.CommitAsync();
        return new(principal, false, codes);
    }

    private async Task FailedAsync(StaffUser user)
    {
        Require(await users.AccessFailedAsync(user));
        if (await users.IsLockedOutAsync(user))
        {
            user.ChallengeId = null;
            user.ChallengeExpiresAt = null;
            Require(await users.UpdateSecurityStampAsync(user));
        }
    }

    public async Task RevokeAsync(ClaimsPrincipal principal)
    {
        var id = principal.FindFirstValue(UserIdClaim)!;
        await using var transaction = await db.Database.BeginTransactionAsync();
        await LockAsync(db, id);
        var user = await users.FindByIdAsync(id);
        if (user is not null)
        {
            user.ChallengeId = null;
            user.ChallengeExpiresAt = null;
            Require(await users.UpdateSecurityStampAsync(user));
        }
        await transaction.CommitAsync();
    }

    private static ClaimsPrincipal Intermediate(StaffUser user)
    {
        var claims = BaseClaims(user);
        claims.Add(new(ChallengeClaim, user.ChallengeId!));
        if (!user.TwoFactorEnabled) claims.Add(new(EnrollmentClaim, "true"));
        return new(new ClaimsIdentity(claims, "ClinicStaffIntermediate"));
    }

    private async Task<ClaimsPrincipal> FullAsync(StaffUser user)
    {
        var claims = BaseClaims(user);
        claims.Add(new("amr", "mfa"));
        claims.AddRange((await users.GetRolesAsync(user)).Where(Roles.Contains).Select(r => new Claim(ClaimTypes.Role, r)));
        claims.AddRange((await users.GetClaimsAsync(user)).Where(c =>
            c.Type == "permission" && Permissions.Contains(c.Value) ||
            (c.Type is "appointment_doctor_id" or "schedule_doctor_id" or "patient_record_id") && Guid.TryParse(c.Value, out var id) && id != Guid.Empty));
        return new(new ClaimsIdentity(claims, "ClinicStaff"));
    }

    private static List<Claim> BaseClaims(StaffUser user) =>
        [new(ClaimTypes.NameIdentifier, user.Id), new(UserIdClaim, user.Id), new(StampClaim, user.SecurityStamp!)];

    internal static void Require(IdentityResult result)
    {
        if (!result.Succeeded) throw new InvalidOperationException("Staff identity operation failed: " +
            string.Join(", ", result.Errors.Select(x => x.Code)));
    }
}
