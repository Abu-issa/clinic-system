using System.Security.Claims;
using Clinic.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Clinic.Infrastructure.Authentication;

// Local operator API only. Never registered as an HTTP endpoint.
public sealed class StaffAdministration(ClinicDbContext db, UserManager<StaffUser> users, RoleManager<IdentityRole> roles)
{
    public async Task<string> ProvisionFirstDoctorAsync(string userName, string password, Guid doctorId, Guid[] scopes)
    {
        await using var tx = await db.Database.BeginTransactionAsync();
        await StaffAuthentication.LockAsync(db, "initial-provisioning");
        await ValidateScopesAsync(scopes);
        if (!await db.Doctors.AnyAsync(x => x.Id == doctorId)) throw new ArgumentException("Doctor ID must identify an existing record.");
        var existing = await users.FindByNameAsync(userName);
        if (existing is not null)
        {
            var expected = Grants(StaffAuthentication.InitialPermissions, scopes).Select(c => c.Type + "=" + c.Value).Order().ToArray();
            var actual = (await users.GetClaimsAsync(existing)).Select(c => c.Type + "=" + c.Value).Order().ToArray();
            if (existing.AssociatedDoctorId != doctorId || !(await users.GetRolesAsync(existing)).SequenceEqual(["Doctor"]) ||
                !expected.SequenceEqual(actual)) throw new InvalidOperationException("Existing account differs; use the explicit administrative procedure.");
            return existing.Id; // No credential, grant, key, stamp or status changes on rerun.
        }
        if (await db.Users.AnyAsync()) throw new InvalidOperationException("Initial provisioning is only available before the first staff account exists.");
        foreach (var role in StaffAuthentication.Roles)
            if (!await roles.RoleExistsAsync(role)) StaffAuthentication.Require(await roles.CreateAsync(new IdentityRole(role)));
        var user = new StaffUser { UserName = userName, AssociatedDoctorId = doctorId, LockoutEnabled = true };
        StaffAuthentication.Require(await users.CreateAsync(user, password));
        StaffAuthentication.Require(await users.AddToRoleAsync(user, "Doctor"));
        StaffAuthentication.Require(await users.AddClaimsAsync(user, Grants(StaffAuthentication.InitialPermissions, scopes)));
        await tx.CommitAsync();
        return user.Id;
    }

    public async Task ChangeAsync(string userId, string operation, string? password = null,
        string[]? approvedRoles = null, string[]? permissions = null, Guid[]? scopes = null, Guid[]? patientScopes = null)
    {
        await using var tx = await db.Database.BeginTransactionAsync();
        await StaffAuthentication.LockAsync(db, userId);
        var user = await users.FindByIdAsync(userId) ?? throw new ArgumentException("Staff ID does not exist.");
        switch (operation)
        {
            case "disable": user.IsEnabled = false; break;
            case "revoke": break;
            case "reset-password":
                var token = await users.GeneratePasswordResetTokenAsync(user);
                StaffAuthentication.Require(await users.ResetPasswordAsync(user, token, password!));
                break;
            case "reset-mfa":
                StaffAuthentication.Require(await users.SetTwoFactorEnabledAsync(user, false));
                StaffAuthentication.Require(await users.ResetAuthenticatorKeyAsync(user));
                await users.GenerateNewTwoFactorRecoveryCodesAsync(user, 0);
                break;
            case "set-grants":
                if (approvedRoles is null || approvedRoles.Length == 0 || approvedRoles.Except(StaffAuthentication.Roles).Any() ||
                    permissions is null || permissions.Except(StaffAuthentication.Permissions).Any()) throw new ArgumentException("Unsupported grants.");
                scopes ??= [];
                if (scopes.Length > 0) await ValidateScopesAsync(scopes);
                patientScopes ??= [];
                if (patientScopes.Contains(Guid.Empty) || patientScopes.Distinct().Count() != patientScopes.Length ||
                    await db.Patients.CountAsync(x => patientScopes.Contains(x.Id)) != patientScopes.Length)
                    throw new ArgumentException("Explicit patient scopes must identify existing patients.");
                foreach (var role in approvedRoles)
                    if (!await roles.RoleExistsAsync(role)) StaffAuthentication.Require(await roles.CreateAsync(new IdentityRole(role)));
                StaffAuthentication.Require(await users.RemoveFromRolesAsync(user, await users.GetRolesAsync(user)));
                StaffAuthentication.Require(await users.AddToRolesAsync(user, approvedRoles));
                StaffAuthentication.Require(await users.RemoveClaimsAsync(user, await users.GetClaimsAsync(user)));
                StaffAuthentication.Require(await users.AddClaimsAsync(user, Grants(permissions, scopes!).Concat(patientScopes.Select(id => new Claim("patient_record_id", id.ToString())))));
                break;
            default: throw new ArgumentException("Unsupported administrative operation.");
        }
        user.ChallengeId = null;
        user.ChallengeExpiresAt = null;
        StaffAuthentication.Require(await users.UpdateSecurityStampAsync(user));
        await tx.CommitAsync();
    }

    private async Task ValidateScopesAsync(Guid[] scopes)
    {
        if (scopes is null || scopes.Length == 0 || scopes.Contains(Guid.Empty) || scopes.Distinct().Count() != scopes.Length ||
            await db.Doctors.CountAsync(x => scopes.Contains(x.Id)) != scopes.Length)
            throw new ArgumentException("Explicit unique scopes must identify existing Doctor records.");
    }

    private static IEnumerable<Claim> Grants(string[] permissions, Guid[] scopes) =>
        permissions.Distinct().Select(x => new Claim("permission", x)).Concat(scopes.SelectMany(id => new[]
        { new Claim("appointment_doctor_id", id.ToString()), new Claim("schedule_doctor_id", id.ToString()) }));
}
