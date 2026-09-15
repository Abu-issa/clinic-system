using Microsoft.AspNetCore.Identity;

namespace Clinic.Infrastructure.Authentication;

public sealed class StaffUser : IdentityUser
{
    public bool IsEnabled { get; set; } = true;
    public Guid? AssociatedDoctorId { get; set; }
    public string? ChallengeId { get; set; }
    public DateTimeOffset? ChallengeExpiresAt { get; set; }
}
