using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Clinic.Api.Audit;
using Clinic.Application.ClinicalTests;
using Clinic.Application.Attachments;
using Clinic.Application.Storage;
using Clinic.Domain.Enums;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Clinic.Api.Controllers;

[ApiController]
[Route("api/staff/patients/{patientId:guid}/test-requests")]
[Authorize(Policy = "StaffSession")]
[RequestSizeLimit(32768)]
public sealed class StaffClinicalTestsController(ClinicalTestService service, IAuthorizationService authorization,
    IAntiforgery antiforgery, HttpAccessAudit audit, ClinicalTestLifecycleService lifecycle, IClinicalTestStore store) : ControllerBase
{
    public sealed record CreateBody([Required] ClinicalTestCategory? Category,
        [Required, StringLength(200)] string? TestName, Guid? VisitId,
        [StringLength(2000)] string? ClinicalInstructions);

    [HttpPost]
    public async Task<IResult> Create(Guid patientId, [FromBody] CreateBody body, CancellationToken ct)
    {
        if (patientId == Guid.Empty) return Error(400, "invalid_input");
        if (!await Allowed(patientId, "ClinicalTestWrite")) return Results.Forbid();
        try { await antiforgery.ValidateRequestAsync(HttpContext); }
        catch (AntiforgeryValidationException) { return Error(400, "invalid_csrf_token"); }
        var actor = User.FindFirstValue("staff_id") ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (actor is null) return Results.Unauthorized();
        if (body.Category is null || body.TestName is null) return Error(400, "invalid_input");
        var result = await service.CreateAsync(new(patientId, body.VisitId, body.Category.Value,
            body.TestName, body.ClinicalInstructions), actor, ct);
        if (result.Error != ClinicalTestError.None) return MapError(result.Error);
        var d = result.Details!;
        return Results.Created($"/api/staff/patients/{patientId}/test-requests/{d.Id}",
            new ClinicalTestSummary(d.Id, d.PatientId, d.VisitId, d.Category, d.TestName, d.Status, d.RequestedAtUtc));
    }

    [HttpGet]
    public async Task<IResult> List(Guid patientId, [FromQuery] ClinicalTestCategory? category = null,
        [FromQuery] ClinicalTestStatus? status = null, [FromQuery] int page = 1, [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        if (patientId == Guid.Empty) return Error(400, "invalid_input");
        if (!await Allowed(patientId, "ClinicalTestRead")) return Results.Forbid();
        var result = await service.ListAsync(patientId, category, status, page, pageSize, ct);
        if (result.Error != ClinicalTestError.None) return MapError(result.Error);
        return await audit.RecordAsync(HttpContext, "test-request.list", "patient", patientId.ToString("N"), patientId)
            ?? Results.Ok(new { items = result.Items, page, pageSize });
    }

    [HttpGet("{requestId:guid}")]
    public async Task<IResult> Get(Guid patientId, Guid requestId, CancellationToken ct)
    {
        if (patientId == Guid.Empty) return Error(400, "invalid_input");
        if (!await Allowed(patientId, "ClinicalTestRead")) return Results.Forbid();
        var result = await service.GetAsync(patientId, requestId, ct);
        if (result.Error != ClinicalTestError.None) return MapError(result.Error);
        var details = result.Details!;
        if (await Allowed(patientId, "AttachmentRead"))
            details = details with { ResultAttachments = await store.ResultsAsync(patientId, requestId, ct) };
        return await audit.RecordAsync(HttpContext, "test-request.read", "test-request", requestId.ToString("N"), patientId)
            ?? Results.Ok(details);
    }

    public sealed record ReviewBody(byte[] ExpectedRowVersion);

    [HttpPost("{requestId:guid}/results")]
    [AttachmentUploadGate]
    public async Task<IResult> Upload(Guid patientId, Guid requestId, CancellationToken ct)
    {
        if (!await Allowed(patientId, "ClinicalTestResultWrite") || !await Allowed(patientId, "AttachmentWrite"))
            return Results.Forbid();
        if (!Request.HasFormContentType || !Request.ContentType!.StartsWith("multipart/form-data", StringComparison.OrdinalIgnoreCase))
            return Error(415, "unsupported_content_type");
        // Parse under the resource gate's bounded stream. Explicit parsing preserves 413;
        // MVC's automatic form binding would convert a stream size exception into 400.
        IFormCollection form;
        try { form = await Request.ReadFormAsync(ct); }
        catch (BadHttpRequestException ex) when (ex.StatusCode == 413) { return Error(413, "file_too_large"); }
        catch (InvalidDataException)
        {
            return HttpContext.Items.ContainsKey(AttachmentUploadGateAttribute.RequestLimitExceededKey)
                ? Error(413, "file_too_large") : Error(400, "invalid_input");
        }
        var file = form.Files.GetFile("file");
        if (file is null || file.Length == 0 || form.Files.Count != 1 || form["expectedRowVersion"].Count != 1)
            return Error(400, "invalid_input");
        byte[] version;
        try { version = Convert.FromBase64String(form["expectedRowVersion"].ToString()); }
        catch (FormatException) { return Error(400, "invalid_row_version"); }
        if (version.Length != 8) return Error(400, "invalid_row_version");
        var actor = User.FindFirstValue("staff_id") ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (actor is null) return Results.Unauthorized();
        try
        {
            var result = await lifecycle.UploadAsync(patientId, requestId, version, actor, User.IsInRole("Doctor"),
                file.OpenReadStream(), file.FileName, file.ContentType, ct);
            return result.Error == ClinicalTestError.None ? Results.Ok(result.Details) : MapError(result.Error);
        }
        catch (FileSizeLimitExceededException) { return Error(413, "file_too_large"); }
        catch (UnsupportedAttachmentTypeException) { return Error(415, "unsupported_file_type"); }
        catch (EmptyFileException) { return Error(400, "invalid_input"); }
        catch (ArgumentException) { return Error(400, "invalid_input"); }
    }

    [HttpPost("{requestId:guid}/review/start")]
    public Task<IResult> StartReview(Guid patientId, Guid requestId, [FromBody] ReviewBody body, CancellationToken ct) =>
        Review(patientId, requestId, body, false, ct);

    [HttpPost("{requestId:guid}/review/complete")]
    public Task<IResult> CompleteReview(Guid patientId, Guid requestId, [FromBody] ReviewBody body, CancellationToken ct) =>
        Review(patientId, requestId, body, true, ct);

    private async Task<IResult> Review(Guid patientId, Guid requestId, ReviewBody body, bool complete, CancellationToken ct)
    {
        if (!await Allowed(patientId, "ClinicalTestWrite")) return Results.Forbid();
        try { await antiforgery.ValidateRequestAsync(HttpContext); }
        catch (AntiforgeryValidationException) { return Error(400, "invalid_csrf_token"); }
        var actor = User.FindFirstValue("staff_id") ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (actor is null) return Results.Unauthorized();
        var result = await lifecycle.ReviewAsync(patientId, requestId, body.ExpectedRowVersion, actor, complete, ct);
        return result.Error == ClinicalTestError.None ? Results.Ok(result.Details) : MapError(result.Error);
    }

    private async Task<bool> Allowed(Guid patientId, string policy) =>
        (await authorization.AuthorizeAsync(User, patientId, policy)).Succeeded;
    private IResult MapError(ClinicalTestError error) => error switch
    {
        ClinicalTestError.PatientNotFound => Error(404, "patient_not_found"),
        ClinicalTestError.VisitNotFound or ClinicalTestError.RequestNotFound => Error(404, "test_request_not_found"),
        ClinicalTestError.DoctorAuthority => Results.Forbid(),
        ClinicalTestError.InvalidTransition => Error(409, "invalid_transition"),
        ClinicalTestError.Conflict => Error(409, "concurrency_conflict"),
        _ => Error(400, "invalid_input"),
    };
    private IResult Error(int status, string code) => Results.Problem(statusCode: status,
        title: "The clinical test request could not be completed.",
        extensions: new Dictionary<string, object?> { ["code"] = code, ["traceId"] = HttpContext.TraceIdentifier });
}
