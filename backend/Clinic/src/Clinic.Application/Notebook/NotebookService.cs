using Clinic.Application.Audit;
using Clinic.Application.Exceptions;
using Clinic.Domain.Entities;
using Clinic.Domain.Enums;

namespace Clinic.Application.Notebook;

// Staff authorization belongs at the API boundary. The actor and author doctor must come from
// its trusted principal; timestamps are server-generated. Page lookup is always bound to the
// authorized route patient so cross-patient page IDs resolve to "not found".
public sealed class NotebookService(INotebookStore store, TimeProvider clock, IAuditMutationWriter auditWriter)
{
    // Bounded pagination, matching the patient-context search limits.
    public const int MaxPageNumber = 100;
    public const int MinPageSize = 1;
    public const int MaxPageSize = 20;

    public async Task<NotebookResult> FinalizeAsync(Guid patientId, Guid pageId, byte[] version,
        Guid doctorId, string actor, CancellationToken ct)
    {
        if (version.Length != 8) return NotebookResult.Failure(NotebookError.InvalidInput);
        using var auditScope = auditWriter.BeginMutation();
        try
        {
            await using var tx = await store.BeginRevisionAsync(patientId, pageId, ct);
            var page = tx.Page;
            if (page is null) return NotebookResult.Failure(NotebookError.PageNotFound);
            // A repeated finalize is a no-op even when its original rowversion is stale.
            if (page.FinalizedAtUtc is not null) return NotebookResult.Success(page.ToDetails());
            if (!page.RowVersion.SequenceEqual(version)) return NotebookResult.Failure(NotebookError.PageChanged);
            if (!await store.DoctorExistsAsync(doctorId, ct)) return NotebookResult.Failure(NotebookError.DoctorNotFound);
            page.FinalizePage(doctorId, clock.GetUtcNow());
            store.ExpectVersion(page, version);
            auditWriter.Append(new(actor, "notebook.page.finalize", "notebook-page", page.Id.ToString("N"),
                patientId, AuditOutcome.Succeeded, null, null));
            await store.SaveRevisionAsync(ct);
            await tx.CommitAsync(ct);
            return NotebookResult.Success(page.ToDetails());
        }
        catch (PersistenceConcurrencyException) { return NotebookResult.Failure(NotebookError.PageChanged); }
        finally { store.DiscardChanges(); }
    }

    public async Task<NotebookResult> CreateAsync(CreateNotebookPageRequest request, string actor, CancellationToken ct = default)
    {
        using var auditScope = auditWriter.BeginMutation();
        try
        {
            var page = new NotebookPage(request.PatientId, request.VisitId, request.AuthorDoctorId,
                request.Title, actor, clock.GetUtcNow(), request.ClientDraftId, request.OriginDeviceId);
            if (!await store.PatientExistsAsync(request.PatientId, ct)) return Fail(NotebookError.PatientNotFound);
            if (!await store.DoctorExistsAsync(request.AuthorDoctorId, ct)) return Fail(NotebookError.DoctorNotFound);
            if (request.VisitId is { } visitId)
            {
                var visitPatientId = await store.VisitPatientIdAsync(visitId, ct);
                if (visitPatientId is null) return Fail(NotebookError.VisitNotFound);
                if (visitPatientId != request.PatientId) return Fail(NotebookError.VisitMismatch);
            }
            store.Add(page);
            auditWriter.Append(new(actor, "notebook.page.create", "notebook-page",
                page.Id.ToString("N"), page.PatientId, AuditOutcome.Succeeded, null, null));
            await store.SaveAsync(ct);
            return NotebookResult.Success(page.ToDetails());
        }
        catch (ArgumentException) { return Fail(NotebookError.InvalidInput); }
        catch { store.DiscardChanges(); throw; }
    }

    public async Task<NotebookResult> GetAsync(Guid patientId, Guid pageId, CancellationToken ct = default)
    {
        var page = await store.GetPageAsync(patientId, pageId, ct);
        return page is null ? NotebookResult.Failure(NotebookError.PageNotFound) : NotebookResult.Success(page.ToDetails());
    }

    public async Task<NotebookListResult> ListAsync(Guid patientId, int page, int pageSize, CancellationToken ct = default)
    {
        if (page is < 1 or > MaxPageNumber || pageSize is < MinPageSize or > MaxPageSize)
            return NotebookListResult.Empty(NotebookError.InvalidInput);
        if (!await store.PatientExistsAsync(patientId, ct)) return NotebookListResult.Empty(NotebookError.PatientNotFound);
        // The store projects one extra lookahead row to determine hasMore; it is never returned.
        var rows = await store.ListPagesAsync(patientId, page, pageSize, ct);
        var hasMore = rows.Count > pageSize;
        return new NotebookListResult(true, NotebookError.None,
            hasMore ? rows.Take(pageSize).ToArray() : rows, page, pageSize, hasMore);
    }

    private NotebookResult Fail(NotebookError error) { store.DiscardChanges(); return NotebookResult.Failure(error); }
}
