using System.Security.Claims;
using Clinic.Infrastructure.Authentication;
using Clinic.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Clinic.IntegrationTests.Api;

// Legacy authorization/appointment tests deliberately construct ticket claims (including invalid
// actors/roles). Back them with revocable staff records without bypassing the production validator.
internal static class PersistedTicketStaff
{
    public static IEnumerable<Claim> Add(WebApplicationFactory<Program> factory, IEnumerable<Claim> claims)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ClinicDbContext>();
        var id = Guid.NewGuid().ToString();
        var user = new StaffUser { Id = id, UserName = id, NormalizedUserName = id.ToUpperInvariant(),
            SecurityStamp = Guid.NewGuid().ToString(), TwoFactorEnabled = true };
        // A unique role record avoids shared-fixture concurrent role-creation races.
        var role = new IdentityRole { Id = Guid.NewGuid().ToString(), Name = "Doctor" };
        db.AddRange(user, role, new IdentityUserRole<string> { UserId = id, RoleId = role.Id });
        db.SaveChanges();
        return claims.Concat(new[] { new Claim(StaffAuthentication.UserIdClaim, id), new Claim(StaffAuthentication.StampClaim, user.SecurityStamp) });
    }
}
