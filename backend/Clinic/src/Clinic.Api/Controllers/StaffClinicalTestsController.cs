using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Clinic.Api.Audit;
using Clinic.Application.ClinicalTests;
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
    IAntiforgery antiforgery, HttpAccessAudit audit) : ControllerBase
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
        return await audit.RecordAsync(HttpContext, "test-request.read", "test-request", requestId.ToString("N"), patientId)
            ?? Results.Ok(result.Details);
    }

    private async Task<bool> Allowed(Guid patientId, string policy) =>
        (await authorization.AuthorizeAsync(User, patientId, policy)).Succeeded;
    private IResult MapError(ClinicalTestError error) => error switch
    {
        ClinicalTestError.PatientNotFound => Error(404, "patient_not_found"),
        ClinicalTestError.VisitNotFound or ClinicalTestError.RequestNotFound => Error(404, "test_request_not_found"),
        ClinicalTestError.DoctorAuthority => Results.Forbid(),
        _ => Error(400, "invalid_input"),
    };
    private IResult Error(int status, string code) => Results.Problem(statusCode: status,
        title: "The clinical test request could not be completed.",
        extensions: new Dictionary<string, object?> { ["code"] = code, ["traceId"] = HttpContext.TraceIdentifier });
}
