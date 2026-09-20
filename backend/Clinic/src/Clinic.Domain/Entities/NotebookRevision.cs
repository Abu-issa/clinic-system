using Clinic.Domain.Enums;

namespace Clinic.Domain.Entities;

// Immutable revision metadata. Payload bytes live only in private StoredFile storage.
public sealed class NotebookRevision
{
    public const int MaxClientDraftIdLength = 128;
    public const int MaxOriginDeviceIdLength = 128;

    public Guid Id { get; private set; }

    public Guid PageId { get; private set; }

    public long RevisionNumber { get; private set; }

    public string AuthorStaffId { get; private set; } = null!;

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public NotebookRevisionKind Kind { get; private set; }

    // Client-supplied idempotency token; null when the caller did not deduplicate.
    public string? ClientDraftId { get; private set; }

    public string? OriginDeviceId { get; private set; }

    public Guid? StoredFileId { get; private set; }

    private NotebookRevision() { } // EF Core

    public NotebookRevision(Guid pageId, long revisionNumber, string authorStaffId, DateTimeOffset createdAtUtc,
        NotebookRevisionKind kind, string? clientDraftId, string? originDeviceId, Guid? storedFileId = null)
    {
        if (pageId == Guid.Empty)
            throw new ArgumentException("A page identifier is required.", nameof(pageId));
        if (revisionNumber < 1)
            throw new ArgumentException("Revision numbers start at 1.", nameof(revisionNumber));
        AuthorStaffId = Actor(authorStaffId);
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind));
        PageId = pageId;
        RevisionNumber = revisionNumber;
        CreatedAtUtc = createdAtUtc.ToUniversalTime();
        Kind = kind;
        ClientDraftId = Optional(clientDraftId, MaxClientDraftIdLength, nameof(clientDraftId));
        OriginDeviceId = Optional(originDeviceId, MaxOriginDeviceIdLength, nameof(originDeviceId));
        if (kind is NotebookRevisionKind.Payload or NotebookRevisionKind.Amendment && (storedFileId is null || storedFileId == Guid.Empty ||
            ClientDraftId is null || OriginDeviceId is null) || kind == NotebookRevisionKind.Created && storedFileId is not null)
            throw new ArgumentException("Invalid revision payload metadata.");
        StoredFileId = storedFileId;
        Id = Guid.NewGuid();
    }

    private static string Actor(string actor)
    {
        if (string.IsNullOrWhiteSpace(actor) || actor.Trim().Length > 450)
            throw new ArgumentException("A valid staff identifier is required.", nameof(actor));
        return actor.Trim();
    }

    private static string? Optional(string? value, int maxLength, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        if (trimmed.Length > maxLength)
            throw new ArgumentException($"{fieldName} cannot exceed {maxLength} characters.", nameof(value));
        return trimmed;
    }
}
