namespace Clinic.Infrastructure.Authentication;

// Keep consumed digests for replay detection for the entire session family.
// No raw refresh credential, including predecessors, is ever persisted.
public sealed class MobileStaffRefreshToken
{
    public string TokenHash { get; set; } = "";
    public Guid SessionId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? ConsumedAtUtc { get; set; }
}
