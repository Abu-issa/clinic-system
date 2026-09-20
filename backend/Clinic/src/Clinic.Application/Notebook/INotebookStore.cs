using Clinic.Domain.Entities;

namespace Clinic.Application.Notebook;

public interface INotebookStore
{
    Task<bool> PatientExistsAsync(Guid patientId, CancellationToken ct = default);
    Task<bool> DoctorExistsAsync(Guid doctorId, CancellationToken ct = default);
    Task<Guid?> VisitPatientIdAsync(Guid visitId, CancellationToken ct = default);
    Task<NotebookPage?> GetPageAsync(Guid patientId, Guid pageId, CancellationToken ct = default);
    Task<IReadOnlyList<NotebookPageListItem>> ListPagesAsync(Guid patientId, int page, int pageSize, CancellationToken ct = default);
    void Add(NotebookPage page);
    Task SaveAsync(CancellationToken ct = default);
    void DiscardChanges();
    Task<INotebookRevisionTransaction> BeginRevisionAsync(Guid patientId, Guid pageId, CancellationToken ct);
    Task<NotebookRevisionReference?> DraftAsync(Guid pageId, string clientDraftId, CancellationToken ct);
    Task<NotebookRevisionReference?> PayloadAsync(Guid patientId, Guid pageId, long revisionNumber, CancellationToken ct);
    void AddPayload(StoredFile file, NotebookRevision revision, NotebookPage page, byte[] expectedVersion);
    void ExpectVersion(NotebookPage page, byte[] expectedVersion);
    Task SaveRevisionAsync(CancellationToken ct);
    Task<bool> RevisionCommittedAsync(Guid revisionId, CancellationToken ct);
}

public interface INotebookRevisionTransaction : IAsyncDisposable
{
    NotebookPage? Page { get; }
    Task CommitAsync(CancellationToken ct);
}

// Internal persistence projection; never serialize this reference at the API boundary.
public sealed record NotebookRevisionReference(NotebookRevision Revision, StoredFile? File);
