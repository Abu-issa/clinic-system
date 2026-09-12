namespace Clinic.Domain.Entities;

public sealed class AppointmentReschedule
{
    private AppointmentReschedule()
    {
    }

    internal AppointmentReschedule(
        Guid appointmentId,
        DateTimeOffset previousStartsAtUtc,
        DateTimeOffset previousEndsAtUtc,
        DateTimeOffset newStartsAtUtc,
        DateTimeOffset newEndsAtUtc,
        string reason,
        string changedByUserId,
        DateTimeOffset changedAtUtc)
    {
        Id = Guid.NewGuid();
        AppointmentId = appointmentId;
        PreviousStartsAtUtc = previousStartsAtUtc;
        PreviousEndsAtUtc = previousEndsAtUtc;
        NewStartsAtUtc = newStartsAtUtc;
        NewEndsAtUtc = newEndsAtUtc;
        Reason = reason;
        ChangedByUserId = changedByUserId;
        ChangedAtUtc = changedAtUtc;
    }

    public Guid Id { get; private set; }

    public Guid AppointmentId { get; private set; }

    public DateTimeOffset PreviousStartsAtUtc { get; private set; }

    public DateTimeOffset PreviousEndsAtUtc { get; private set; }

    public DateTimeOffset NewStartsAtUtc { get; private set; }

    public DateTimeOffset NewEndsAtUtc { get; private set; }

    public string Reason { get; private set; } = string.Empty;

    public string ChangedByUserId { get; private set; } = string.Empty;

    public DateTimeOffset ChangedAtUtc { get; private set; }
}
