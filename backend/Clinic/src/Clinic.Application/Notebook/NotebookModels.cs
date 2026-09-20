using Clinic.Domain.Entities;
using Clinic.Domain.Enums;

namespace Clinic.Application.Notebook;

public enum NotebookError { None, InvalidInput, PatientNotFound, DoctorNotFound, VisitNotFound, VisitMismatch, PageNotFound, PageChanged }

public sealed record NotebookResult(bool IsSuccess, NotebookError Error, NotebookPageDetails? Details = null)
{
    public static NotebookResult Success(NotebookPageDetails details) => new(true, NotebookError.None, details);
    public static NotebookResult Failure(NotebookError error) => new(false, error);
}

public sealed record CreateNotebookPageRequest(Guid PatientId, Guid? VisitId, Guid AuthorDoctorId,
    string Title, string? ClientDraftId, string? OriginDeviceId);

public sealed record NotebookPageDetails(Guid Id, Guid PatientId, Guid? VisitId, Guid AuthorDoctorId, string Title,
    DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc, DateTimeOffset? FinalizedAtUtc, Guid? FinalizedByDoctorId,
    long CurrentRevisionNumber, byte[] RowVersion);

public sealed record NotebookPageListItem(Guid Id, Guid PatientId, Guid? VisitId, Guid AuthorDoctorId, string Title,
    DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc, long CurrentRevisionNumber);

public sealed record NotebookListResult(bool IsSuccess, NotebookError Error,
    IReadOnlyList<NotebookPageListItem> Items, int Page, int PageSize, bool HasMore)
{
    public static NotebookListResult Empty(NotebookError error) => new(false, error, [], 0, 0, false);
}

internal static class NotebookMappingExtensions
{
    internal static NotebookPageDetails ToDetails(this NotebookPage page) => new(
        page.Id, page.PatientId, page.VisitId, page.AuthorDoctorId, page.Title,
        page.CreatedAtUtc, page.UpdatedAtUtc, page.FinalizedAtUtc, page.FinalizedByDoctorId,
        page.CurrentRevisionNumber, page.RowVersion);

    internal static NotebookPageListItem ToListItem(this NotebookPage page) => new(
        page.Id, page.PatientId, page.VisitId, page.AuthorDoctorId, page.Title,
        page.CreatedAtUtc, page.UpdatedAtUtc, page.CurrentRevisionNumber);
}
