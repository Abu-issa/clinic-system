using System.Security.Claims;
using Clinic.Api.Audit;
using Clinic.Api.StaffMvc;
using Clinic.Application.Attachments;
using Clinic.Application.ClinicalTests;
using Clinic.Application.Storage;
using Clinic.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Clinic.Api.Controllers;

[Authorize(Policy = "StaffSession")]
[StaffUiResultFilter]
[Route("staff/patients/{patientId:guid}/tests")]
public sealed class StaffTestsMvcController(ClinicalTestService service, ClinicalTestLifecycleService lifecycle,
    IClinicalTestStore store, IAuthorizationService authorization, HttpAccessAudit audit,
    AttachmentOptions options, StaffText text, StaffCreateSubmissions submissions) : Controller
{
    private string Actor => User.FindFirstValue("staff_id") ?? User.FindFirstValue(ClaimTypes.NameIdentifier)!;
    private async Task<bool> Allowed(Guid patient, string policy) =>
        (await authorization.AuthorizeAsync(User, patient, policy)).Succeeded;
    private IActionResult Error(ClinicalTestError error) => StaffUiErrors.Result(error switch {
        ClinicalTestError.DoctorAuthority => 403,
        ClinicalTestError.PatientNotFound or ClinicalTestError.VisitNotFound or ClinicalTestError.RequestNotFound => 404,
        _ => 400 }, error == ClinicalTestError.InvalidInput ? "Invalid" : null);
    private async Task<bool> Audit(Guid patient, Guid? request = null) =>
        await audit.RecordAsync(HttpContext, request is null ? "test-request.list" : "test-request.read",
            request is null ? "patient" : "test-request", (request ?? patient).ToString("N"), patient) is null;

    [HttpGet]
    public async Task<IActionResult> Index([FromRoute] Guid patientId, ClinicalTestCategory? category, ClinicalTestStatus? status,
        int page = 1, int pageSize = 20, CancellationToken ct = default)
    {
        if (!await Allowed(patientId, "ClinicalTestRead")) return Forbid();
        if (!ModelState.IsValid) return StaffUiErrors.Result(400, "Invalid");
        var result = await service.ListAsync(patientId, category, status, page, pageSize, ct);
        if (result.Error != ClinicalTestError.None) return Error(result.Error);
        var patient = await service.PatientContextAsync(patientId, ct);
        if (patient is null) return StaffUiErrors.Result(404);
        if (!await Audit(patientId)) return Unauthorized();
        return View(new StaffTestListPage(patient, result.Items, page, pageSize, category, status,
            await Allowed(patientId, "ClinicalTestWrite")));
    }

    [HttpGet("create")]
    public async Task<IActionResult> Create([FromRoute] Guid patientId, CancellationToken ct)
    {
        if (!await Allowed(patientId, "ClinicalTestRead") || !await Allowed(patientId, "ClinicalTestWrite")) return Forbid();
        var patient = await service.PatientContextAsync(patientId, ct);
        if (patient is null) return StaffUiErrors.Result(404);
        IReadOnlyList<ClinicalTestVisitChoice> visits = [];
        if (await Allowed(patientId, "VisitRead"))
        {
            visits = await service.VisitChoicesAsync(patientId, Actor, ct);
            if (await audit.RecordAsync(HttpContext, "visit.list", "patient", patientId.ToString("N"), patientId) is not null)
                return Unauthorized();
        }
        if (!await Audit(patientId)) return Unauthorized();
        return View(new StaffTestCreatePage(patient, visits, submissions.Issue(Actor, patientId)));
    }

    [HttpPost("create"), ValidateAntiForgeryToken, RequestSizeLimit(32768)]
    public async Task<IActionResult> Create([FromRoute] Guid patientId, [FromForm] CreateStaffTestForm form, CancellationToken ct)
    {
        if (!await Allowed(patientId, "ClinicalTestWrite")) return Forbid();
        if (!ModelState.IsValid || form.Category is null || form.TestName is null)
            return CreatedRedirect(patientId, "Invalid");
        if (!submissions.Consume(form.SubmissionToken, Actor, patientId))
            return StaffUiErrors.Result(409, "SubmissionExpired");
        var result = await service.CreateAsync(new(patientId, form.VisitId, form.Category.Value,
            form.TestName, form.ClinicalInstructions), Actor, ct);
        if (result.Error == ClinicalTestError.InvalidInput) return CreatedRedirect(patientId, "Invalid");
        if (result.Error != ClinicalTestError.None) return Error(result.Error);
        return DetailRedirect(patientId, result.Details!.Id, "Saved");
    }

    [HttpGet("{requestId:guid}")]
    public async Task<IActionResult> Detail([FromRoute] Guid patientId, [FromRoute] Guid requestId, CancellationToken ct)
    {
        if (!await Allowed(patientId, "ClinicalTestRead")) return Forbid();
        var result = await service.GetAsync(patientId, requestId, ct);
        if (result.Error != ClinicalTestError.None) return Error(result.Error);
        var patient = await service.PatientContextAsync(patientId, ct);
        if (patient is null) return StaffUiErrors.Result(404);
        var d = result.Details!;
        var attachments = await Allowed(patientId, "AttachmentRead") ? await store.ResultsAsync(patientId, requestId, ct) : null;
        var upload = await Allowed(patientId, "ClinicalTestResultWrite") && await Allowed(patientId, "AttachmentWrite") &&
            await lifecycle.CheckUploadAsync(patientId, requestId, Actor, User.IsInRole("Doctor"), ct) == ClinicalTestError.None;
        var review = await Allowed(patientId, "ClinicalTestWrite") && await lifecycle.HasDoctorAuthorityAsync(patientId, requestId, Actor, ct);
        if (!await Audit(patientId, requestId)) return Unauthorized();
        return View(new StaffTestDetailPage(patient, d, attachments, upload, review, options.MaxFileSizeBytes));
    }

    [HttpPost("{requestId:guid}/results"), AttachmentUploadGate]
    public async Task<IActionResult> Upload([FromRoute] Guid patientId, [FromRoute] Guid requestId, CancellationToken ct)
    {
        // The shared resource filter checks CSRF and permissions before any multipart read.
        if (!Request.HasFormContentType || !Request.ContentType!.StartsWith("multipart/form-data", StringComparison.OrdinalIgnoreCase))
            return DetailRedirect(patientId, requestId, "Unsupported");
        IFormCollection form;
        try { form = await Request.ReadFormAsync(ct); }
        catch (BadHttpRequestException ex) when (ex.StatusCode == 413) { return DetailRedirect(patientId, requestId, "TooLarge"); }
        catch (InvalidDataException) { return DetailRedirect(patientId, requestId,
            HttpContext.Items.ContainsKey(AttachmentUploadGateAttribute.RequestLimitExceededKey) ? "TooLarge" : "Invalid"); }
        var file = form.Files.GetFile("file");
        if (file is null || file.Length == 0 || form.Files.Count != 1 || form["expectedRowVersion"].Count != 1 ||
            !TryVersion(form["expectedRowVersion"].ToString(), out var version))
            return DetailRedirect(patientId, requestId, "Invalid");
        try
        {
            var result = await lifecycle.UploadAsync(patientId, requestId, version, Actor, User.IsInRole("Doctor"),
                file.OpenReadStream(), file.FileName, file.ContentType, ct);
            return MutationResult(patientId, requestId, result.Error);
        }
        catch (FileSizeLimitExceededException) { return DetailRedirect(patientId, requestId, "TooLarge"); }
        catch (UnsupportedAttachmentTypeException) { return DetailRedirect(patientId, requestId, "Unsupported"); }
        catch (EmptyFileException) { return DetailRedirect(patientId, requestId, "Invalid"); }
        catch (ArgumentException) { return DetailRedirect(patientId, requestId, "Invalid"); }
    }

    [HttpPost("{requestId:guid}/review/start"), ValidateAntiForgeryToken, RequestSizeLimit(32768)]
    public Task<IActionResult> StartReview([FromRoute] Guid patientId, [FromRoute] Guid requestId, [FromForm] string? expectedRowVersion, CancellationToken ct) =>
        Review(patientId, requestId, expectedRowVersion, false, ct);
    [HttpPost("{requestId:guid}/review/complete"), ValidateAntiForgeryToken, RequestSizeLimit(32768)]
    public Task<IActionResult> CompleteReview([FromRoute] Guid patientId, [FromRoute] Guid requestId, [FromForm] string? expectedRowVersion, CancellationToken ct) =>
        Review(patientId, requestId, expectedRowVersion, true, ct);
    private async Task<IActionResult> Review(Guid patientId, Guid requestId, string? token, bool complete, CancellationToken ct)
    {
        if (!await Allowed(patientId, "ClinicalTestWrite")) return Forbid();
        if (!ModelState.IsValid || !TryVersion(token, out var version)) return DetailRedirect(patientId, requestId, "Invalid");
        return MutationResult(patientId, requestId, (await lifecycle.ReviewAsync(patientId, requestId, version, Actor, complete, ct)).Error);
    }
    private IActionResult MutationResult(Guid patient, Guid request, ClinicalTestError error) => error switch {
        ClinicalTestError.None => DetailRedirect(patient, request, "Saved"),
        ClinicalTestError.Conflict => DetailRedirect(patient, request, "Conflict"),
        ClinicalTestError.InvalidTransition => DetailRedirect(patient, request, "Transition"),
        ClinicalTestError.InvalidInput => DetailRedirect(patient, request, "Invalid"),
        _ => Error(error) };
    private IActionResult DetailRedirect(Guid patientId, Guid requestId, string notice)
    {
        TempData["Notice"] = notice; // Only a catalog key; never clinical input or filenames in cookies.
        return RedirectToAction(nameof(Detail), new { patientId, requestId, culture = text.Culture });
    }
    private IActionResult CreatedRedirect(Guid patientId, string notice)
    {
        TempData["Notice"] = notice;
        return RedirectToAction(nameof(Create), new { patientId, culture = text.Culture });
    }
    private static bool TryVersion(string? token, out byte[] version)
    {
        version = [];
        if (string.IsNullOrWhiteSpace(token) || token.Length > 32) return false;
        try { version = Convert.FromBase64String(token); return version.Length == 8; }
        catch (FormatException) { return false; }
    }
}
