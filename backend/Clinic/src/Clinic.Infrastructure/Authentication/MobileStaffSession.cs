namespace Clinic.Infrastructure.Authentication;

// Only a SHA-256 digest of the random bearer credential is persisted.
public sealed class MobileStaffSession
{
    public Guid Id { get; set; }
    public string StaffUserId { get; set; } = "";
    public string TokenHash { get; set; } = "";
    public string ProtectedPrincipal { get; set; } = "";
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
    // Null for pre-refresh sessions: the migration does not grant them refresh capability.
    public DateTimeOffset? RefreshExpiresAtUtc { get; set; }
    public DateTimeOffset? RevokedAtUtc { get; set; }
}
