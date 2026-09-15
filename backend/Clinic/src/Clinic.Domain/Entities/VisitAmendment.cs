namespace Clinic.Domain.Entities;

public sealed class VisitAmendment
{
    private VisitAmendment() { }
    internal VisitAmendment(Guid visitId, string reason, string content, string actor, DateTimeOffset now)
    {
        Visit.Actor(actor);
        Reason = Visit.Text(reason) ?? throw new ArgumentException("An amendment reason is required.");
        AmendmentText = Visit.Text(content) ?? throw new ArgumentException("Amendment content is required.");
        Id = Guid.NewGuid(); VisitId = visitId; CreatedByStaffId = actor; CreatedAtUtc = now.ToUniversalTime();
    }
    public Guid Id { get; private set; }
    public Guid VisitId { get; private set; }
    public string Reason { get; private set; } = null!;
    public string AmendmentText { get; private set; } = null!;
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public string CreatedByStaffId { get; private set; } = null!;
}
