using System.Security.Claims;
using Clinic.Api.Audit;
using Clinic.Api.Notebook;
using Clinic.Application.Notebook;
using Clinic.Infrastructure.Authentication;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Clinic.Application.Storage;
using Microsoft.AspNetCore.Http.Features;
using System.Security.Cryptography;

namespace Clinic.Api.Controllers;

// Notebook metadata and versioned payloads. Reads fail closed on audit failure;
// mutations and their audit events share one database transaction.
[ApiController]
[Route("api/staff/patients/{patientId:guid}/notebook/pages")]
[Authorize(AuthenticationSchemes = "ClinicNotebookStaff", Policy = "StaffSession")]
[RequestSizeLimit(4096)]
public sealed class StaffNotebookPagesController(
    NotebookService service,
    IAuthorizationService authorization,
    IAntiforgery antiforgery,
    UserManager<StaffUser> users,
    HttpAccessAudit audit, StaffAuthentication staff, NotebookPayloadService payloads,
    INotebookStore store, IFileStorage storage) : ControllerBase
{
    [HttpGet("{pageId:guid}/revisions")]
    public async Task<IResult> Revisions(Guid patientId, Guid pageId, [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20, CancellationToken ct = default)
    {
        if (!await Allowed(patientId, "NotebookRead")) return Results.Forbid();
        if (page < 1 || page > 1000000 || pageSize < 1 || pageSize > 100) return Error(400, "invalid_input");
        var notebook = await store.GetPageAsync(patientId, pageId, ct);
        if (notebook is null) return Error(404, "page_not_found");
        var revisions = await store.ListRevisionsAsync(pageId, page, pageSize, ct);
        var projection = new { patientId, pageId, page, pageSize, hasMore = revisions.Count > pageSize,
            currentRevisionNumber = Math.Max(notebook.CurrentRevisionNumber, revisions.FirstOrDefault()?.RevisionNumber ?? 0),
            items = revisions.Take(pageSize).Select(r => new {
                r.RevisionNumber, r.AuthorStaffId, r.CreatedAtUtc, r.HasPayload,
                kind = r.Kind == Clinic.Domain.Enums.NotebookRevisionKind.Payload ? "Revision" : r.Kind.ToString()
            }).ToArray() };
        return await audit.RecordAsync(HttpContext, "notebook.revision.list", "notebook-page",
            pageId.ToString("N"), patientId) ?? Results.Ok(projection);
    }

    [HttpPost("{pageId:guid}/revisions")]
    [NotebookMultipart]
    [RequestSizeLimit(NotebookPayloadService.MaxRequestBytes)]
    public async Task<IResult> SaveRevision(Guid patientId, Guid pageId, CancellationToken ct)
        => await UploadRevision(patientId, pageId, false, ct);

    [HttpPost("{pageId:guid}/amendments")]
    [NotebookMultipart]
    [RequestSizeLimit(NotebookPayloadService.MaxRequestBytes)]
    public async Task<IResult> Amend(Guid patientId, Guid pageId, CancellationToken ct)
        => await UploadRevision(patientId, pageId, true, ct);

    [HttpPost("{pageId:guid}/finalize")]
    public async Task<IResult> FinalizePage(Guid patientId, Guid pageId, [FromBody] FinalizeNotebookBody body, CancellationToken ct)
    {
        if (!await Allowed(patientId, "NotebookWrite")) return Results.Forbid();
        if (!await Csrf()) return CsrfError();
        if (body.ExpectedRowVersion is not { Length: 8 }) return Error(400, "invalid_row_version");
        var actor = GetActor();
        if (actor is null) return Results.Unauthorized();
        var user = await users.FindByIdAsync(actor);
        if (user?.AssociatedDoctorId is not { } doctorId) return Results.Forbid();
        var result = await service.FinalizeAsync(patientId, pageId, body.ExpectedRowVersion, doctorId, actor, ct);
        return result.IsSuccess ? Results.Ok(Map(result.Details!)) : MapError(result.Error);
    }

    private async Task<IResult> UploadRevision(Guid patientId, Guid pageId, bool amendment, CancellationToken ct)
    {
        if (!await Allowed(patientId, "NotebookWrite")) return Results.Forbid();
        if (!Request.Headers.ContainsKey("Authorization") &&
            (string.IsNullOrWhiteSpace(Request.Headers["X-CSRF-TOKEN"]) || !await Csrf())) return CsrfError();
        if (await store.GetPageAsync(patientId, pageId, ct) is null) return Error(404, "page_not_found");
        if (!Request.HasFormContentType || Request.ContentLength > NotebookPayloadService.MaxRequestBytes) return Error(400, "invalid_input");
        HttpContext.Features.Set<IFormFeature>(new FormFeature(Request, new FormOptions
        {
            MultipartBodyLengthLimit = NotebookPayloadService.MaxBytes, ValueLengthLimit = 256,
            ValueCountLimit = 4, MultipartHeadersLengthLimit = 2048, MemoryBufferThreshold = NotebookPayloadService.MaxRequestBytes
        }));
        var original = Request.Body;
        Request.Body = new AttachmentUploadGateAttribute.LimitedRequestStream(original, NotebookPayloadService.MaxRequestBytes, () => { });
        try
        {
            var form = await Request.ReadFormAsync(ct);
            if (form.Count != 3 || form.Files.Count != 1 || form.Files[0].Name != "payload" ||
                form["expectedRowVersion"].Count != 1 || form["clientDraftId"].Count != 1 || form["originDeviceId"].Count != 1)
                return Error(400, "invalid_input");
            byte[] version;
            try { version = Convert.FromBase64String(form["expectedRowVersion"].ToString()); }
            catch (FormatException) { return Error(400, "invalid_row_version"); }
            await using var input = form.Files[0].OpenReadStream();
            var result = await payloads.SaveAsync(patientId, pageId, version, form["clientDraftId"].ToString(),
                form["originDeviceId"].ToString(), GetActor()!, input, ct, amendment);
            if (result.Error is { } error) return Error(error switch
            {
                "page_not_found" => 404, "page_changed" or "draft_conflict" or "invalid_lifecycle" => 409, _ => 400
            }, error);
            var response = new { result.RevisionId, result.RevisionNumber, result.RowVersion, result.Replayed };
            return result.Replayed ? Results.Ok(response) : Results.Created(
                $"/api/staff/patients/{patientId}/notebook/pages/{pageId}/revisions/{result.RevisionNumber}/payload", response);
        }
        catch (FileSizeLimitExceededException) { return Error(413, "payload_too_large"); }
        catch (EmptyFileException) { return Error(400, "invalid_payload"); }
        catch (InvalidDataException) { return Error(400, "invalid_multipart"); }
        catch (BadHttpRequestException ex) { return Error(ex.StatusCode, "invalid_multipart"); }
        finally { Request.Body = original; }
    }

    [HttpGet("{pageId:guid}/revisions/{revisionNumber:long}/payload")]
    public async Task<IResult> ReadPayload(Guid patientId, Guid pageId, long revisionNumber, CancellationToken ct)
    {
        if (!await Allowed(patientId, "NotebookRead")) return Results.Forbid();
        var reference = await store.PayloadAsync(patientId, pageId, revisionNumber, ct);
        if (reference?.File is not { } file) return Error(404, "revision_not_found");
        await using var input = await storage.OpenReadAsync(file.StorageKey, ct);
        if (input is null) return Error(503, "backing_object_missing");
        byte[] bytes;
        try { bytes = await NotebookPayloadService.ReadBoundedAsync(input, ct); }
        catch (FileSizeLimitExceededException) { return Error(503, "payload_integrity_failure"); }
        if (bytes.LongLength != file.SizeBytes ||
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() != file.Sha256 ||
            !NotebookPayloadService.ValidPayload(bytes, patientId, pageId)) return Error(503, "payload_integrity_failure");
        var failure = await audit.RecordAsync(HttpContext, "notebook.revision.read", "notebook-revision",
            reference.Revision.Id.ToString("N"), patientId);
        if (failure is not null) return failure;
        Response.Headers["X-Notebook-Patient"] = patientId.ToString();
        Response.Headers["X-Notebook-Page"] = pageId.ToString();
        Response.Headers["X-Notebook-Revision"] = reference.Revision.RevisionNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Response.Headers.XContentTypeOptions = "nosniff";
        var realInk = NotebookInkPayload.Valid(bytes, patientId, pageId);
        Response.Headers.ContentDisposition = realInk ? "attachment; filename=notebook.msgpack" : "attachment; filename=notebook.json";
        return Results.Stream(new MemoryStream(bytes, writable: false), realInk ? "application/msgpack" : "application/json");
    }

    [HttpPost]
    public async Task<IResult> Create(Guid patientId, [FromBody] CreateNotebookPageBody body, CancellationToken ct)
    {
        if (!await Allowed(patientId, "NotebookWrite")) return Results.Forbid();
        if (!await Csrf()) return CsrfError();
        if (body is null || string.IsNullOrWhiteSpace(body.Title)) return Error(400, "invalid_input");

        var actor = GetActor();
        if (actor is null) return Results.Unauthorized();
        var user = await users.FindByIdAsync(actor);
        // The author is the authenticated doctor's associated record, never a body-supplied ID.
        if (user?.AssociatedDoctorId is not { } authorDoctorId) return Results.Forbid();

        var request = new CreateNotebookPageRequest(patientId, body.VisitId, authorDoctorId,
            body.Title, body.ClientDraftId, body.OriginDeviceId);
        var result = await service.CreateAsync(request, actor, ct);
        return result.IsSuccess
            ? Results.Created($"/api/staff/patients/{patientId}/notebook/pages/{result.Details!.Id}",
                Map(result.Details))
            : MapError(result.Error);
    }

    [HttpGet]
    public async Task<IResult> List(Guid patientId, [FromQuery] int page = 1, [FromQuery] int pageSize = 10,
        CancellationToken ct = default)
    {
        if (!await Allowed(patientId, "NotebookRead")) return Results.Forbid();
        var result = await service.ListAsync(patientId, page, pageSize, ct);
        if (!result.IsSuccess) return MapError(result.Error);
        var projection = new NotebookPageListResponse(
            result.Items.Select(x => new NotebookPageListItemResponse(x.Id, x.PatientId, x.VisitId,
                x.AuthorDoctorId, x.Title, x.CreatedAtUtc, x.UpdatedAtUtc, x.CurrentRevisionNumber)).ToArray(),
            result.Page, result.PageSize, result.HasMore);
        return await audit.RecordAsync(HttpContext, "notebook.page.list", "patient",
            patientId.ToString("N"), patientId)
            ?? Results.Ok(projection);
    }

    [HttpGet("{pageId:guid}")]
    public async Task<IResult> Get(Guid patientId, Guid pageId, CancellationToken ct)
    {
        if (!await Allowed(patientId, "NotebookRead")) return Results.Forbid();
        var result = await service.GetAsync(patientId, pageId, ct);
        if (!result.IsSuccess) return MapError(result.Error);
        return await audit.RecordAsync(HttpContext, "notebook.page.read", "notebook-page",
            pageId.ToString("N"), patientId)
            ?? Results.Ok(Map(result.Details!));
    }

    private async Task<bool> Allowed(Guid patientId, string policy) =>
        (await authorization.AuthorizeAsync(
            await staff.NotebookPrincipalAsync(User, HttpContext.RequestAborted), patientId, policy)).Succeeded;

    private async Task<bool> Csrf()
    {
        // The selector authenticates Authorization-bearing requests exclusively via the mobile handler.
        if (Request.Headers.ContainsKey("Authorization")) return true;
        try { await antiforgery.ValidateRequestAsync(HttpContext); return true; }
        catch (AntiforgeryValidationException) { return false; }
    }

    private string? GetActor() =>
        User.FindFirst("staff_id")?.Value ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

    private IResult MapError(NotebookError error) => error switch
    {
        NotebookError.PageChanged => Error(409, "page_changed"),
        NotebookError.PatientNotFound => Error(404, "patient_not_found"),
        NotebookError.DoctorNotFound => Error(404, "doctor_not_found"),
        NotebookError.VisitNotFound => Error(404, "visit_not_found"),
        NotebookError.VisitMismatch => Error(409, "visit_mismatch"),
        NotebookError.PageNotFound => Error(404, "page_not_found"),
        _ => Error(400, "invalid_input")
    };

    private IResult CsrfError() => Results.Problem(
        statusCode: 400,
        title: "The request verification token is invalid.",
        extensions: new Dictionary<string, object?> { ["code"] = "invalid_csrf_token", ["traceId"] = HttpContext.TraceIdentifier });

    private IResult Error(int status, string code) => Results.Problem(
        statusCode: status,
        title: "The notebook request could not be completed.",
        extensions: new Dictionary<string, object?> { ["code"] = code, ["traceId"] = HttpContext.TraceIdentifier });

    private static NotebookPageResponse Map(NotebookPageDetails d) => new(
        d.Id, d.PatientId, d.VisitId, d.AuthorDoctorId, d.Title,
        d.CreatedAtUtc, d.UpdatedAtUtc, d.FinalizedAtUtc, d.FinalizedByDoctorId,
        d.CurrentRevisionNumber, d.RowVersion);
}
