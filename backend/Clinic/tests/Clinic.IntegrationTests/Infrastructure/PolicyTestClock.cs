namespace Clinic.IntegrationTests.Infrastructure;

public sealed class PolicyTestClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2030, 1, 7, 4, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => Now;
}
