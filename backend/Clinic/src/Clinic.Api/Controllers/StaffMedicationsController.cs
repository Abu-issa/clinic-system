using Clinic.Api.Medications;
using Clinic.Application.Medications;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Clinic.Api.Controllers;

/// <summary>
/// Clinic-wide medication catalog. Reading requires Doctor/DoctorAssistant with the persisted
/// medications.read permission; mutations additionally require Doctor with medications.manage.
/// Catalog authority is clinic-wide: scheduling scopes and patient scopes never grant it.
/// </summary>
[ApiController]
[Route("api/staff/medications")]
[Authorize(Policy = "MedicationRead")]
[RequestSizeLimit(131072)]
public sealed class StaffMedicationsController(
    MedicationCatalogService service,
    IAuthorizationService authorization,
    IAntiforgery antiforgery) : ControllerBase
{
    [HttpGet]
    public async Task<IResult> List(
        [FromQuery] string? query, [FromQuery] bool? activeOnly,
        [FromQuery] int skip = 0, [FromQuery] int take = 50, CancellationToken ct = default)
    {
        var result = await service.SearchAsync(new MedicationSearchQuery(query, activeOnly, skip, take), ct);
        return result.IsSuccess
            ? Results.Ok(result.Items.Select(m => new MedicationListItemResponse(
                m.Id, m.GenericNameEn, m.GenericNameAr, m.BrandNameEn, m.BrandNameAr,
                m.Strength, m.Unit, m.Form, m.Route, m.Category, m.IsActive)).ToArray())
            : MapError(result.Error);
    }

    [HttpGet("{medicationId:guid}")]
    public async Task<IResult> Get(Guid medicationId, CancellationToken ct)
    {
        var result = await service.GetAsync(medicationId, ct);
        return result.IsSuccess ? Results.Ok(Map(result.Details!)) : MapError(result.Error);
    }

    [HttpPost]
    public async Task<IResult> Create([FromBody] MedicationUpsertBody body, CancellationToken ct)
    {
        if (!await ManageAllowed()) return Results.Forbid();
        if (!await Csrf()) return CsrfError();
        var actor = GetActor();
        if (actor is null) return Results.Unauthorized();

        var result = await service.CreateAsync(ToRequest(body), actor, ct);
        return result.IsSuccess
            ? Results.Created($"/api/staff/medications/{result.Details!.Id}", Map(result.Details))
            : MapError(result.Error);
    }

    [HttpPut("{medicationId:guid}")]
    public async Task<IResult> Update(Guid medicationId, [FromBody] MedicationUpsertBody body, CancellationToken ct)
    {
        if (!await ManageAllowed()) return Results.Forbid();
        if (!await Csrf()) return CsrfError();
        if (body.ExpectedRowVersion is not { Length: 8 }) return Error(400, "invalid_row_version");
        var actor = GetActor();
        if (actor is null) return Results.Unauthorized();

        var request = new UpdateMedicationRequest(
            body.GenericNameEn, body.GenericNameAr, body.BrandNameEn, body.BrandNameAr,
            body.Strength!, body.Unit!, body.Form, body.Route, body.Category, body.ExpectedRowVersion);
        var result = await service.UpdateAsync(medicationId, request, actor, ct);
        return result.IsSuccess ? Results.Ok(Map(result.Details!)) : MapError(result.Error);
    }

    [HttpPost("{medicationId:guid}/deactivate")]
    public async Task<IResult> Deactivate(Guid medicationId, [FromBody] MedicationVersionBody body, CancellationToken ct)
    {
        if (!await ManageAllowed()) return Results.Forbid();
        if (!await Csrf()) return CsrfError();
        if (body.ExpectedRowVersion is not { Length: 8 }) return Error(400, "invalid_row_version");
        var actor = GetActor();
        if (actor is null) return Results.Unauthorized();

        var result = await service.DeactivateAsync(medicationId, body.ExpectedRowVersion, actor, ct);
        return result.IsSuccess ? Results.Ok(Map(result.Details!)) : MapError(result.Error);
    }

    [HttpPost("{medicationId:guid}/activate")]
    public async Task<IResult> Activate(Guid medicationId, [FromBody] MedicationVersionBody body, CancellationToken ct)
    {
        if (!await ManageAllowed()) return Results.Forbid();
        if (!await Csrf()) return CsrfError();
        if (body.ExpectedRowVersion is not { Length: 8 }) return Error(400, "invalid_row_version");
        var actor = GetActor();
        if (actor is null) return Results.Unauthorized();

        var result = await service.ActivateAsync(medicationId, body.ExpectedRowVersion, actor, ct);
        return result.IsSuccess ? Results.Ok(Map(result.Details!)) : MapError(result.Error);
    }

    private async Task<bool> ManageAllowed() =>
        (await authorization.AuthorizeAsync(User, HttpContext, "MedicationManage")).Succeeded;

    private async Task<bool> Csrf()
    {
        try { await antiforgery.ValidateRequestAsync(HttpContext); return true; }
        catch (AntiforgeryValidationException) { return false; }
    }

    private string? GetActor() =>
        User.FindFirst("staff_id")?.Value ?? User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;

    private static CreateMedicationRequest ToRequest(MedicationUpsertBody body) => new(
        body.GenericNameEn, body.GenericNameAr, body.BrandNameEn, body.BrandNameAr,
        body.Strength!, body.Unit!, body.Form, body.Route, body.Category);

    private IResult MapError(MedicationCatalogError error) => error switch
    {
        MedicationCatalogError.MedicationNotFound => Error(404, "medication_not_found"),
        MedicationCatalogError.InvalidRowVersion => Error(400, "invalid_row_version"),
        MedicationCatalogError.MedicationChanged => Error(409, "medication_changed"),
        MedicationCatalogError.DuplicateMedication => Error(409, "duplicate_medication"),
        _ => Error(400, "invalid_input")
    };

    private IResult CsrfError() => Results.Problem(
        statusCode: 400,
        title: "The request verification token is invalid.",
        extensions: new Dictionary<string, object?> { ["code"] = "invalid_csrf_token", ["traceId"] = HttpContext.TraceIdentifier });

    private IResult Error(int status, string code) => Results.Problem(
        statusCode: status,
        title: "The medication catalog request could not be completed.",
        extensions: new Dictionary<string, object?> { ["code"] = code, ["traceId"] = HttpContext.TraceIdentifier });

    private static MedicationResponse Map(MedicationDetails m) => new(
        m.Id, m.GenericNameEn, m.GenericNameAr, m.BrandNameEn, m.BrandNameAr,
        m.Strength, m.Unit, m.Form, m.Route, m.Category, m.IsActive,
        m.CreatedAtUtc, m.LastModifiedAtUtc, m.RowVersion);
}
