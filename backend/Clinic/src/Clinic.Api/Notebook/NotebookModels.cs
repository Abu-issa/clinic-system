namespace Clinic.Api.Notebook;

public sealed record FinalizeNotebookBody(byte[]? ExpectedRowVersion);

// Body binds only the fields the client may supply: title and optional visit/draft/device
// references. Author doctor, actor and timestamps are server-authoritative.
public sealed record CreateNotebookPageBody(string? Title, Guid? VisitId, string? ClientDraftId, string? OriginDeviceId);

public sealed record NotebookPageResponse(Guid Id, Guid PatientId, Guid? VisitId, Guid AuthorDoctorId, string Title,
    DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc, DateTimeOffset? FinalizedAtUtc, Guid? FinalizedByDoctorId,
    long CurrentRevisionNumber, byte[] RowVersion);

public sealed record NotebookPageListItemResponse(Guid Id, Guid PatientId, Guid? VisitId, Guid AuthorDoctorId,
    string Title, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc, long CurrentRevisionNumber);

public sealed record NotebookPageListResponse(IReadOnlyList<NotebookPageListItemResponse> Items,
    int Page, int PageSize, bool HasMore);
