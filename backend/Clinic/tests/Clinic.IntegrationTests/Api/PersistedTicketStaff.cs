using System.Security.Claims;
using Clinic.Infrastructure.Authentication;
using Clinic.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Clinic.IntegrationTests.Api;

// Legacy authorization/appointment tests deliberately construct ticket claims (including invalid
// actors/roles). Back them with revocable staff records without bypassing the production validator:
// every claim the per-request persisted-grant revalidation checks (roles, permissions, scheduling
// and patient scopes) is persisted exactly as the ticket carries it.
internal static class PersistedTicketStaff
{
    public static IEnumerable<Claim> Add(WebApplicationFactory<Program> factory, IEnumerable<Claim> claims)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ClinicDbContext>();
        var id = Guid.NewGuid().ToString();
        var materialized = claims.ToList();
        var user = new StaffUser { Id = id, UserName = id, NormalizedUserName = id.ToUpperInvariant(),
            SecurityStamp = Guid.NewGuid().ToString(), TwoFactorEnabled = true };
        db.Add(user);

        // Role claims must be backed by the role store or revalidation rejects the session; a
        // real staff role is always included so the session itself remains valid. Roles keep
        // NormalizedName unset: unique role records avoid shared-fixture role-creation races.
        var roleNames = materialized.Where(c => c.Type == ClaimTypes.Role).Select(c => c.Value).ToList();
        roleNames.Add("Doctor");
        foreach (var name in roleNames.Distinct())
        {
            var role = new IdentityRole { Id = Guid.NewGuid().ToString(), Name = name };
            db.AddRange(role, new IdentityUserRole<string> { UserId = id, RoleId = role.Id });
        }

        foreach (var claim in materialized.Where(c => c.Type is "permission" or "patient_record_id"
                     or "appointment_doctor_id" or "schedule_doctor_id"))
            db.Add(new IdentityUserClaim<string> { UserId = id, ClaimType = claim.Type, ClaimValue = claim.Value });

        db.SaveChanges();
        return materialized.Concat(new[]
        {
            new Claim(StaffAuthentication.UserIdClaim, id),
            new Claim(StaffAuthentication.StampClaim, user.SecurityStamp)
        });
    }
}
