using Clinic.Domain.Enums;

namespace Clinic.Domain.Entities;

// Notebook aggregate. Finalization is permanent; subsequent content is appended as amendments.
public sealed class NotebookPage
{
    public const int MaxTitleLength = 200;

    private readonly List<NotebookRevision> revisions = [];

    public Guid Id { get; private set; }

    public Guid PatientId { get; private set; }

    public Guid? VisitId { get; private set; }

    public Guid AuthorDoctorId { get; private set; }

    public string Title { get; private set; } = null!;

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public DateTimeOffset? FinalizedAtUtc { get; private set; }

    public Guid? FinalizedByDoctorId { get; private set; }

    public long CurrentRevisionNumber { get; private set; }

    public byte[] RowVersion { get; private set; } = [];

    public IReadOnlyCollection<NotebookRevision> Revisions => revisions.AsReadOnly();

    private NotebookPage() { } // EF Core

    // Creation always records revision 1: a page never exists without its created revision.
    // Author identity and timestamps come from the trusted caller (server-authoritative actor/time).
    public NotebookPage(Guid patientId, Guid? visitId, Guid authorDoctorId, string title,
        string actorStaffId, DateTimeOffset now, string? clientDraftId, string? originDeviceId)
    {
        if (patientId == Guid.Empty || authorDoctorId == Guid.Empty || visitId == Guid.Empty)
            throw new ArgumentException("Valid page identifiers are required.");
        Title = RequiredText(title, MaxTitleLength);
        var author = NotebookRevisionActor(actorStaffId);
        Id = Guid.NewGuid();
        PatientId = patientId;
        VisitId = visitId;
        AuthorDoctorId = authorDoctorId;
        CreatedAtUtc = UpdatedAtUtc = now.ToUniversalTime();
        CurrentRevisionNumber = 1;
        revisions.Add(new NotebookRevision(Id, 1, author, now, NotebookRevisionKind.Created,
            clientDraftId, originDeviceId));
    }

    public NotebookRevision AppendPayload(Guid storedFileId, string actor, DateTimeOffset now,
        string clientDraftId, string originDeviceId)
    {
        if (FinalizedAtUtc is not null) throw new InvalidOperationException("Page is finalized.");
        return AppendRevision(storedFileId, actor, now, clientDraftId, originDeviceId, NotebookRevisionKind.Payload);
    }

    public bool FinalizePage(Guid doctorId, DateTimeOffset now)
    {
        if (doctorId == Guid.Empty) throw new ArgumentException("A doctor is required.", nameof(doctorId));
        if (FinalizedAtUtc is not null) return false;
        FinalizedAtUtc = UpdatedAtUtc = now.ToUniversalTime();
        FinalizedByDoctorId = doctorId;
        return true;
    }

    public NotebookRevision AppendAmendment(Guid storedFileId, string actor, DateTimeOffset now,
        string clientDraftId, string originDeviceId)
    {
        if (FinalizedAtUtc is null) throw new InvalidOperationException("Page must be finalized.");
        return AppendRevision(storedFileId, actor, now, clientDraftId, originDeviceId, NotebookRevisionKind.Amendment);
    }

    private NotebookRevision AppendRevision(Guid storedFileId, string actor, DateTimeOffset now,
        string clientDraftId, string originDeviceId, NotebookRevisionKind kind)
    {
        var revision = new NotebookRevision(Id, checked(CurrentRevisionNumber + 1), actor, now,
            kind, clientDraftId, originDeviceId, storedFileId);
        revisions.Add(revision);
        CurrentRevisionNumber = revision.RevisionNumber;
        UpdatedAtUtc = now.ToUniversalTime();
        return revision;
    }

    private static string RequiredText(string value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("A title is required.", nameof(value));
        var trimmed = value.Trim();
        if (trimmed.Length > maxLength)
            throw new ArgumentException($"Title cannot exceed {maxLength} characters.", nameof(value));
        return trimmed;
    }

    private static string NotebookRevisionActor(string actor)
    {
        if (string.IsNullOrWhiteSpace(actor) || actor.Trim().Length > 450)
            throw new ArgumentException("A valid staff identifier is required.", nameof(actor));
        return actor.Trim();
    }
}
