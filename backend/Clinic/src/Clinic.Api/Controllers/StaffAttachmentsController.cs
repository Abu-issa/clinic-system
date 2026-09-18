using System.Security.Claims;
using Clinic.Api.Audit;
using Clinic.Application.Attachments;
using Clinic.Application.Storage;
using Clinic.Domain.Entities;
using Clinic.Infrastructure.Authentication;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Clinic.Api.Controllers;

[ApiController]
[Route("api/staff/patients/{patientId:guid}")]
[Authorize(Policy = "StaffSession")]
public sealed class StaffAttachmentsController(
    AttachmentService attachments,
    IAttachmentStore store,
    IFileStorage fileStorage,
    IAuthorizationService authorization,
    IAntiforgery antiforgery,
    HttpAccessAudit audit,
    UserManager<StaffUser> users,
    IOptions<AttachmentOptions> options) : ControllerBase
{
    private readonly AttachmentOptions _options = options.Value;

    public sealed record AttachmentResponse(Guid AttachmentId, Guid PatientId, Guid? VisitId,
        string OriginalFileName, string ContentType, long SizeBytes, DateTimeOffset CreatedAtUtc);

    [HttpPost("attachments")]
    [AttachmentUploadGate]
    public async Task<IResult> UploadPatient(Guid patientId, [FromForm] IFormFile? file, CancellationToken ct)
    {
        var (actor, failure) = await PrepareUpload(patientId, file, ct);
        if (failure is not null) return failure;

        return await ExecuteUpload(() => attachments.UploadAsync(
            new UploadAttachmentRequest(patientId, null, file!.OpenReadStream(), file.FileName, file.ContentType, actor!), ct),
            patientId);
    }

    [HttpPost("visits/{visitId:guid}/attachments")]
    [AttachmentUploadGate]
    public async Task<IResult> UploadVisit(Guid patientId, Guid visitId, [FromForm] IFormFile? file, CancellationToken ct)
    {
        var (actor, failure) = await PrepareUpload(patientId, file, ct);
        if (failure is not null) return failure;

        // Visit-specific clinical authority: the visit must exist under the route patient, and
        // Doctors additionally need persisted AssociatedDoctorId == Visit.DoctorId (never
        // appointment/schedule claims or client-supplied IDs). Assistants keep the existing
        // assistant clinical-write rule (patient scope + permission, no doctor association).
        var visit = await store.VisitAsync(patientId, visitId, ct);
        if (visit is null) return Error(404, "attachment_not_found", "The attachment request could not be completed.");
        if (User.IsInRole("Doctor") && !await HasDoctorAuthorityAsync(visit.DoctorId, ct)) return Results.Forbid();

        return await ExecuteUpload(() => attachments.UploadAsync(
            new UploadAttachmentRequest(patientId, visitId, file!.OpenReadStream(), file.FileName, file.ContentType, actor!), ct),
            patientId);
    }

    private async Task<IResult> ExecuteUpload(Func<Task<AttachmentUploadResult>> upload, Guid patientId)
    {
        AttachmentUploadResult result;
        try
        {
            result = await upload();
        }
        catch (FileSizeLimitExceededException)
        {
            // The streamed byte count (never a declared length) exceeded the feature limit.
            return Error(413, "file_too_large", "The attachment request could not be completed.");
        }
        catch (UnsupportedAttachmentTypeException)
        {
            // Extension/MIME pairing or leading signature failed the narrow allowlist.
            return Error(415, "unsupported_file_type", "The attachment request could not be completed.");
        }
        catch (EmptyFileException)
        {
            return Error(400, "invalid_input", "The attachment request could not be completed.");
        }
        return MapUpload(result, patientId);
    }

    [HttpGet("attachments")]
    public async Task<IResult> ListPatient(Guid patientId, [FromQuery] int page = 1, [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        if (!await Allowed(patientId, "AttachmentRead")) return Results.Forbid();
        if (InvalidPage(page, pageSize)) return Error(400, "invalid_input", "The attachment request could not be completed.");
        var items = await store.ListAsync(patientId, null, page, pageSize, ct);
        var projection = items.Select(MapListItem).ToArray();
        return await audit.RecordAsync(HttpContext, "file.list", "patient", patientId.ToString("N"), patientId)
            ?? Results.Ok(new { items = projection, page, pageSize });
    }

    [HttpGet("visits/{visitId:guid}/attachments")]
    public async Task<IResult> ListVisit(Guid patientId, Guid visitId, [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20, CancellationToken ct = default)
    {
        if (!await Allowed(patientId, "AttachmentRead")) return Results.Forbid();
        if (InvalidPage(page, pageSize)) return Error(400, "invalid_input", "The attachment request could not be completed.");
        if (await store.VisitAsync(patientId, visitId, ct) is null)
            return Error(404, "attachment_not_found", "The attachment request could not be completed.");
        var items = await store.ListAsync(patientId, visitId, page, pageSize, ct);
        var projection = items.Select(MapListItem).ToArray();
        return await audit.RecordAsync(HttpContext, "file.list", "visit", visitId.ToString("N"), patientId)
            ?? Results.Ok(new { items = projection, page, pageSize });
    }

    [HttpGet("attachments/{attachmentId:guid}/download")]
    public async Task<IResult> Download(Guid patientId, Guid attachmentId, CancellationToken ct)
    {
        if (!await Allowed(patientId, "AttachmentRead")) return Results.Forbid();
        // Server-side resolution only: patient + attachment ID; the storage key never comes
        // from the client and foreign-patient attachments stay hidden.
        var reference = await store.DownloadAsync(patientId, attachmentId, ct);
        if (reference is null) return Error(404, "attachment_not_found", "The attachment request could not be completed.");

        var content = await fileStorage.OpenReadAsync(reference.StorageKey, ct);
        if (content is null)
            // Metadata without its object is an integrity failure: never 200, never empty bytes,
            // never a successful download audit event, never a physical path disclosure.
            return Error(503, "backing_object_missing", "The attachment request could not be completed.");

        try
        {
            // Fail-closed access audit (Phase 3 model): the bytes are returned only after the
            // file.download event persisted successfully; on failure the stream is disposed.
            var auditFailure = await audit.RecordAsync(HttpContext, "file.download", "attachment",
                reference.AttachmentId.ToString("N"), patientId);
            if (auditFailure is not null)
            {
                await content.DisposeAsync();
                return auditFailure;
            }
            Response.Headers.ContentDisposition = BuildDisposition(reference);
            Response.Headers.XContentTypeOptions = "nosniff";
            return Results.Stream(content, reference.ContentType);
        }
        catch
        {
            await content.DisposeAsync();
            throw;
        }
    }

    // --- shared helpers ---

    private async Task<(string? Actor, IResult? Failure)> PrepareUpload(Guid patientId, IFormFile? file, CancellationToken ct)
    {
        if (file is null || file.Length == 0) return (null, Error(400, "invalid_input", "The attachment request could not be completed."));
        if (!await Allowed(patientId, "AttachmentWrite")) return (null, Results.Forbid());
        // CSRF precedes any storage or database write: a rejected request stores no bytes.
        if (!await Csrf()) return (null, CsrfError());
        var actor = GetActor();
        return actor is null ? (null, Results.Unauthorized()) : (actor, null);
    }

    private IResult MapUpload(AttachmentUploadResult result, Guid patientId) => result switch
    {
        _ when result.Error == AttachmentUploadError.PatientNotFound ||
               result.Error == AttachmentUploadError.VisitMismatch =>
            Error(404, "attachment_not_found", "The attachment request could not be completed."),
        _ when result.Attachment is { } created =>
            Results.Created($"/api/staff/patients/{patientId}/attachments/{created.AttachmentId}",
                new AttachmentResponse(created.AttachmentId, created.PatientId, created.VisitId,
                    created.OriginalFileName, created.ContentType, created.SizeBytes, created.CreatedAtUtc)),
        _ => Error(400, "invalid_input", "The attachment request could not be completed."),
    };

    private static bool InvalidPage(int page, int pageSize) => page is < 1 or > 1000 || pageSize is < 1 or > 100;

    private static IResult Error(int status, string code, string title) =>
        Results.Problem(statusCode: status, title: title,
            extensions: new Dictionary<string, object?> { ["code"] = code });

    private IResult CsrfError() => Results.Problem(
        statusCode: 400,
        title: "The request verification token is invalid.",
        extensions: new Dictionary<string, object?> { ["code"] = "invalid_csrf_token", ["traceId"] = HttpContext.TraceIdentifier });

    private async Task<bool> Allowed(Guid patientId, string policyName)
    {
        var decision = await authorization.AuthorizeAsync(User, patientId, policyName);
        return decision.Succeeded;
    }

    private async Task<bool> Csrf()
    {
        try { await antiforgery.ValidateRequestAsync(HttpContext); return true; }
        catch (AntiforgeryValidationException) { return false; }
    }

    private string? GetActor() =>
        User.FindFirst("staff_id")?.Value ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

    private async Task<bool> HasDoctorAuthorityAsync(Guid doctorId, CancellationToken ct)
    {
        var staffId = GetActor();
        if (staffId is null) return false;
        var user = await users.FindByIdAsync(staffId);
        return user is not null && user.AssociatedDoctorId.HasValue && user.AssociatedDoctorId.Value == doctorId;
    }

    private static object MapListItem(AttachmentListItem item) => new    {
        attachmentId = item.AttachmentId, visitId = item.VisitId, originalFileName = item.OriginalFileName,
        contentType = item.ContentType, sizeBytes = item.SizeBytes, createdAtUtc = item.CreatedAtUtc,
    };

    /// <summary>
    /// Attachment-only disposition with a sanitized name: display metadata is trimmed of
    /// quotes/backslashes/control characters (CRLF header injection impossible) and is emitted
    /// as RFC 5987 filename* with an ASCII fallback; the fallback extension derives from the
    /// validated content type, never from the client filename.
    /// </summary>
    internal static string BuildDisposition(AttachmentDownloadReference reference)
    {
        var name = reference.OriginalFileName.Trim()
            .Where(c => !char.IsControl(c) && c is not '"' and not '\\' and not '/' and not '<' and not '>')
            .ToArray();
        var safe = new string(name).Trim();
        if (safe.Length == 0 || safe.Length > 200) safe = $"attachment-{reference.AttachmentId:N}";
        var fallbackExtension = reference.ContentType.ToLowerInvariant() switch
        {
            "application/pdf" => ".pdf",
            "image/jpeg" => ".jpg",
            "image/png" => ".png",
            _ => ".bin",
        };
        if (!safe.Contains('.', StringComparison.Ordinal)) safe += fallbackExtension;
        var asciiFallback = safe.All(c => c is > (char)32 and < (char)127) ? safe : $"attachment-{reference.AttachmentId:N}{fallbackExtension}";
        return $"attachment; filename=\"{asciiFallback}\"; filename*=UTF-8''{Uri.EscapeDataString(safe)}";
    }
}
