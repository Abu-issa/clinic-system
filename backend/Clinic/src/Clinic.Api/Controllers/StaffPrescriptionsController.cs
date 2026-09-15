using Clinic.Api.Prescriptions;
using Clinic.Application.Prescriptions;
using Clinic.Application.Visits;
using Clinic.Domain.Entities;
using Clinic.Infrastructure.Authentication;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace Clinic.Api.Controllers;

/// <summary>
/// Staff prescription endpoints. Reading requires Doctor + prescriptions.read + the exact
/// persisted patient scope; mutations additionally require the requested operation permission
/// and per-request doctor authority: the persisted StaffUser.AssociatedDoctorId must match the
/// prescribing doctor. appointment_doctor_id and schedule_doctor_id never grant prescription
/// authority, and there is no cross-doctor delegation.
/// </summary>
[ApiController]
[Route("api/staff/patients/{patientId:guid}")]
[Authorize(Policy = "StaffSession")]
[RequestSizeLimit(131072)]
public sealed class StaffPrescriptionsController(
    PrescriptionService service,
    VisitService visits,
    IAuthorizationService authorization,
    IAntiforgery antiforgery,
    UserManager<StaffUser> users) : ControllerBase
{
    [HttpPost("visits/{visitId:guid}/prescriptions")]
    public async Task<IResult> Create(Guid patientId, Guid visitId, [FromBody] CreatePrescriptionBody body, CancellationToken ct)
    {
        if (!await Allowed(patientId, "PrescriptionWrite")) return Results.Forbid();
        if (!await Csrf()) return CsrfError("The prescription request could not be completed.");

        // Route patient must match the stored visit; its prescribing doctor determines authority.
        var visit = await visits.GetAsync(patientId, visitId, ct);
        if (!visit.IsSuccess) return Error(404, "visit_not_found", "The prescription request could not be completed.");
        if (!await HasDoctorAuthorityAsync(visit.Details!.DoctorId, ct)) return Results.Forbid();
        var actor = GetActor();
        if (actor is null) return Results.Unauthorized();

        var result = await service.CreateDraftAsync(new CreatePrescriptionDraftRequest(visitId, body.Notes), actor, ct);
        return result.IsSuccess
            ? Results.Created($"/api/staff/patients/{patientId}/prescriptions/{result.Details!.Id}", Map(result.Details))
            : MapError(result.Error, "The prescription request could not be completed.");
    }

    [HttpGet("visits/{visitId:guid}/prescriptions")]
    public async Task<IResult> ListByVisit(Guid patientId, Guid visitId, CancellationToken ct)
    {
        if (!await Allowed(patientId, "PrescriptionRead")) return Results.Forbid();
        var visit = await visits.GetAsync(patientId, visitId, ct);
        if (!visit.IsSuccess) return Error(404, "visit_not_found", "The prescription request could not be completed.");
        var result = await service.ListByVisitAsync(patientId, visitId, ct);
        return result.IsSuccess
            ? Results.Ok(result.Prescriptions.Select(MapListItem).ToArray())
            : MapError(result.Error, "The prescription request could not be completed.");
    }

    [HttpGet("prescriptions")]
    public async Task<IResult> ListByPatient(Guid patientId, [FromQuery] int skip = 0, [FromQuery] int take = 50, CancellationToken ct = default)
    {
        if (!await Allowed(patientId, "PrescriptionRead")) return Results.Forbid();
        var result = await service.ListByPatientAsync(patientId, skip, take, ct);
        return result.IsSuccess
            ? Results.Ok(result.Prescriptions.Select(MapListItem).ToArray())
            : MapError(result.Error, "The prescription request could not be completed.");
    }

    [HttpGet("prescriptions/{prescriptionId:guid}")]
    public async Task<IResult> Get(Guid patientId, Guid prescriptionId, CancellationToken ct)
    {
        if (!await Allowed(patientId, "PrescriptionRead")) return Results.Forbid();
        var result = await service.GetAsync(patientId, prescriptionId, ct);
        return result.IsSuccess
            ? Results.Ok(Map(result.Details!))
            : MapError(result.Error, "The prescription request could not be completed.");
    }

    [HttpPut("prescriptions/{prescriptionId:guid}/notes")]
    public async Task<IResult> UpdateNotes(Guid patientId, Guid prescriptionId, [FromBody] PrescriptionNotesBody body, CancellationToken ct)
    {
        if (!await Allowed(patientId, "PrescriptionWrite")) return Results.Forbid();
        if (!await Csrf()) return CsrfError("The prescription request could not be completed.");
        if (body.ExpectedRowVersion is not { Length: 8 })
            return Error(400, "invalid_row_version", "The prescription request could not be completed.");

        var (loadError, _) = await LoadForMutation(patientId, prescriptionId, ct);
        if (loadError is not null) return loadError;
        var actor = GetActor();
        if (actor is null) return Results.Unauthorized();

        var result = await service.UpdateDraftNotesAsync(patientId, prescriptionId, new(body.Notes, body.ExpectedRowVersion), actor, ct);
        return Mutated(result);
    }

    [HttpPost("prescriptions/{prescriptionId:guid}/items")]
    public async Task<IResult> AddItem(Guid patientId, Guid prescriptionId, [FromBody] PrescriptionItemBody body, CancellationToken ct)
    {
        if (!await Allowed(patientId, "PrescriptionWrite")) return Results.Forbid();
        if (!await Csrf()) return CsrfError("The prescription request could not be completed.");
        if (body.ExpectedRowVersion is not { Length: 8 } || body.MedicationId is null || body.MedicationId == Guid.Empty)
            return Error(400, "invalid_input", "The prescription request could not be completed.");

        var (loadError, _) = await LoadForMutation(patientId, prescriptionId, ct);
        if (loadError is not null) return loadError;
        var actor = GetActor();
        if (actor is null) return Results.Unauthorized();

        var request = new AddPrescriptionItemRequest(
            body.MedicationId.Value, body.Dose, body.Frequency, body.Duration,
            body.Instructions, body.DisplayOrder, body.ExpectedRowVersion);
        var result = await service.AddItemAsync(patientId, prescriptionId, request, actor, ct);
        return Mutated(result);
    }

    [HttpPut("prescriptions/{prescriptionId:guid}/items/{itemId:guid}")]
    public async Task<IResult> UpdateItem(Guid patientId, Guid prescriptionId, Guid itemId, [FromBody] PrescriptionItemReplaceBody body, CancellationToken ct)
    {
        if (!await Allowed(patientId, "PrescriptionWrite")) return Results.Forbid();
        if (!await Csrf()) return CsrfError("The prescription request could not be completed.");
        if (body.ExpectedRowVersion is not { Length: 8 })
            return Error(400, "invalid_row_version", "The prescription request could not be completed.");

        var (loadError, _) = await LoadForMutation(patientId, prescriptionId, ct);
        if (loadError is not null) return loadError;
        var actor = GetActor();
        if (actor is null) return Results.Unauthorized();

        var request = new UpdatePrescriptionItemRequest(
            body.ReselectedMedicationId, body.Dose, body.Frequency, body.Duration,
            body.Instructions, body.DisplayOrder, body.ExpectedRowVersion);
        var result = await service.UpdateItemAsync(patientId, prescriptionId, itemId, request, actor, ct);
        return Mutated(result);
    }

    [HttpDelete("prescriptions/{prescriptionId:guid}/items/{itemId:guid}")]
    public async Task<IResult> RemoveItem(Guid patientId, Guid prescriptionId, Guid itemId, [FromBody] PrescriptionItemRemoveBody body, CancellationToken ct)
    {
        if (!await Allowed(patientId, "PrescriptionWrite")) return Results.Forbid();
        if (!await Csrf()) return CsrfError("The prescription request could not be completed.");
        if (body.ExpectedRowVersion is not { Length: 8 })
            return Error(400, "invalid_row_version", "The prescription request could not be completed.");

        var (loadError, _) = await LoadForMutation(patientId, prescriptionId, ct);
        if (loadError is not null) return loadError;
        var actor = GetActor();
        if (actor is null) return Results.Unauthorized();

        var result = await service.RemoveItemAsync(patientId, prescriptionId, itemId, body.ExpectedRowVersion, actor, ct);
        return Mutated(result);
    }

    [HttpPut("prescriptions/{prescriptionId:guid}/items/order")]
    public async Task<IResult> ReorderItems(Guid patientId, Guid prescriptionId, [FromBody] PrescriptionItemOrderBody body, CancellationToken ct)
    {
        if (!await Allowed(patientId, "PrescriptionWrite")) return Results.Forbid();
        if (!await Csrf()) return CsrfError("The prescription request could not be completed.");
        if (body.ExpectedRowVersion is not { Length: 8 } || body.OrderedItemIds is null)
            return Error(400, "invalid_input", "The prescription request could not be completed.");

        var (loadError, _) = await LoadForMutation(patientId, prescriptionId, ct);
        if (loadError is not null) return loadError;
        var actor = GetActor();
        if (actor is null) return Results.Unauthorized();

        var request = new ReorderPrescriptionItemsRequest(body.OrderedItemIds, body.ExpectedRowVersion);
        var result = await service.ReorderItemsAsync(patientId, prescriptionId, request, actor, ct);
        return Mutated(result);
    }

    [HttpPost("prescriptions/{prescriptionId:guid}/finalize")]
    public async Task<IResult> Finalize(Guid patientId, Guid prescriptionId, [FromBody] PrescriptionVersionBody body, CancellationToken ct)
    {
        if (!await Allowed(patientId, "PrescriptionFinalize")) return Results.Forbid();
        if (!await Csrf()) return CsrfError("The prescription request could not be completed.");
        if (body.ExpectedRowVersion is not { Length: 8 })
            return Error(400, "invalid_row_version", "The prescription request could not be completed.");

        var (loadError, _) = await LoadForMutation(patientId, prescriptionId, ct);
        if (loadError is not null) return loadError;
        var actor = GetActor();
        if (actor is null) return Results.Unauthorized();

        var result = await service.FinalizeAsync(patientId, prescriptionId, body.ExpectedRowVersion, actor, ct);
        // From the finalize action, InvalidInput means a structurally incomplete draft — a
        // lifecycle conflict rather than a malformed request, so it maps to 409.
        return Mutated(result, invalidInputStatus: 409, invalidInputCode: "incomplete_prescription");
    }

    [HttpPost("prescriptions/{prescriptionId:guid}/release")]
    public async Task<IResult> Release(Guid patientId, Guid prescriptionId, [FromBody] PrescriptionVersionBody body, CancellationToken ct)
    {
        if (!await Allowed(patientId, "PrescriptionRelease")) return Results.Forbid();
        if (!await Csrf()) return CsrfError("The prescription request could not be completed.");
        if (body.ExpectedRowVersion is not { Length: 8 })
            return Error(400, "invalid_row_version", "The prescription request could not be completed.");

        var (loadError, _) = await LoadForMutation(patientId, prescriptionId, ct);
        if (loadError is not null) return loadError;
        var actor = GetActor();
        if (actor is null) return Results.Unauthorized();

        var result = await service.ReleaseAsync(patientId, prescriptionId, body.ExpectedRowVersion, actor, ct);
        return Mutated(result);
    }

    [HttpPost("prescriptions/{prescriptionId:guid}/cancel")]
    public async Task<IResult> Cancel(Guid patientId, Guid prescriptionId, [FromBody] PrescriptionCancelBody body, CancellationToken ct)
    {
        if (!await Allowed(patientId, "PrescriptionCancel")) return Results.Forbid();
        if (!await Csrf()) return CsrfError("The prescription request could not be completed.");
        if (string.IsNullOrWhiteSpace(body.Reason) || body.ExpectedRowVersion is not { Length: 8 })
            return Error(400, "invalid_input", "The prescription request could not be completed.");

        var (loadError, _) = await LoadForMutation(patientId, prescriptionId, ct);
        if (loadError is not null) return loadError;
        var actor = GetActor();
        if (actor is null) return Results.Unauthorized();

        var result = await service.CancelAsync(patientId, prescriptionId, new(body.Reason, body.ExpectedRowVersion), actor, ct);
        return Mutated(result);
    }

    [HttpPost("prescriptions/{prescriptionId:guid}/replacement")]
    public async Task<IResult> CreateReplacement(Guid patientId, Guid prescriptionId, [FromBody] ReplacementBody body, CancellationToken ct)
    {
        if (!await Allowed(patientId, "PrescriptionWrite")) return Results.Forbid();
        if (!await Csrf()) return CsrfError("The prescription request could not be completed.");
        if (body.ExpectedOriginalRowVersion is not { Length: 8 })
            return Error(400, "invalid_row_version", "The prescription request could not be completed.");

        // The route prescription is the cancelled original; its doctor determines authority.
        var (loadError, original) = await LoadForMutation(patientId, prescriptionId, ct);
        if (loadError is not null) return loadError;
        var actor = GetActor();
        if (actor is null) return Results.Unauthorized();

        var result = await service.CreateDraftAsync(
            new CreatePrescriptionDraftRequest(original.VisitId, body.Notes, prescriptionId, body.ExpectedOriginalRowVersion),
            actor, ct);
        return result.IsSuccess
            ? Results.Created($"/api/staff/patients/{patientId}/prescriptions/{result.Details!.Id}", Map(result.Details))
            : MapError(result.Error, "The prescription request could not be completed.");
    }

    // Loads the prescription for an existing-object mutation: binds the route patient, returns
    // 404 for foreign resources, and verifies per-request doctor authority before any mutation.
    private async Task<(IResult? Error, PrescriptionDetails Details)> LoadForMutation(Guid patientId, Guid prescriptionId, CancellationToken ct)
    {
        var existing = await service.GetAsync(patientId, prescriptionId, ct);
        if (!existing.IsSuccess)
            return (MapError(existing.Error, "The prescription request could not be completed."), null!);
        if (!await HasDoctorAuthorityAsync(existing.Details!.DoctorId, ct))
            return (Results.Forbid(), null!);
        return (null, existing.Details);
    }

    private async Task<bool> Allowed(Guid patientId, string policy) =>
        (await authorization.AuthorizeAsync(User, patientId, policy)).Succeeded;

    private async Task<bool> HasDoctorAuthorityAsync(Guid doctorId, CancellationToken ct)
    {
        var staffId = GetActor();
        if (staffId is null) return false;
        var user = await users.FindByIdAsync(staffId);
        return user is { IsEnabled: true } && user.AssociatedDoctorId.HasValue && user.AssociatedDoctorId.Value == doctorId;
    }

    private async Task<bool> Csrf()
    {
        try { await antiforgery.ValidateRequestAsync(HttpContext); return true; }
        catch (AntiforgeryValidationException) { return false; }
    }

    private string? GetActor() =>
        User.FindFirst("staff_id")?.Value ?? User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;

    private IResult Mutated(PrescriptionResult result, int invalidInputStatus = 400, string invalidInputCode = "invalid_input") =>
        result.IsSuccess
            ? Results.Ok(Map(result.Details!))
            : result.Error == PrescriptionError.InvalidInput
                ? Error(invalidInputStatus, invalidInputCode, "The prescription request could not be completed.")
                : MapError(result.Error, "The prescription request could not be completed.");

    private IResult MapError(PrescriptionError error, string title) => error switch
    {
        PrescriptionError.VisitNotFound => Error(404, "visit_not_found", title),
        PrescriptionError.PrescriptionNotFound => Error(404, "prescription_not_found", title),
        PrescriptionError.PrescriptionItemNotFound => Error(404, "prescription_item_not_found", title),
        PrescriptionError.MedicationNotFound => Error(404, "medication_not_found", title),
        PrescriptionError.InvalidRowVersion => Error(400, "invalid_row_version", title),
        PrescriptionError.PrescriptionChanged => Error(409, "prescription_changed", title),
        PrescriptionError.InactiveMedication => Error(409, "inactive_medication", title),
        PrescriptionError.InvalidLifecycle => Error(409, "invalid_lifecycle", title),
        PrescriptionError.ReplacementMismatch => Error(409, "replacement_mismatch", title),
        _ => Error(400, "invalid_input", title)
    };

    private IResult CsrfError(string title) => Results.Problem(
        statusCode: 400,
        title: "The request verification token is invalid.",
        extensions: new Dictionary<string, object?> { ["code"] = "invalid_csrf_token", ["traceId"] = HttpContext.TraceIdentifier });

    private IResult Error(int status, string code, string title) => Results.Problem(
        statusCode: status,
        title: title,
        extensions: new Dictionary<string, object?> { ["code"] = code, ["traceId"] = HttpContext.TraceIdentifier });

    private static PrescriptionListItemResponse MapListItem(PrescriptionListItem p) => new(
        p.Id, p.VisitId, p.PatientId, p.DoctorId, p.Status, p.ItemCount,
        p.CreatedAtUtc, p.FinalizedAtUtc, p.ReleasedAtUtc, p.CancelledAtUtc);

    private static PrescriptionResponse Map(PrescriptionDetails d) => new(
        d.Id, d.VisitId, d.PatientId, d.DoctorId, d.Status,
        d.ReplacesPrescriptionId, d.ReplacedByPrescriptionId, d.Notes,
        d.FinalizedAtUtc, d.ReleasedAtUtc, d.CancellationReason, d.CancelledAtUtc,
        d.CreatedAtUtc, d.LastModifiedAtUtc, d.RowVersion,
        d.Items.Select(i => new PrescriptionItemResponse(
            i.Id, i.MedicationId, i.GenericNameEn, i.GenericNameAr, i.BrandNameEn, i.BrandNameAr,
            i.Strength, i.Unit, i.Form, i.Route, i.Dose, i.Frequency, i.Duration, i.Instructions,
            i.DisplayOrder, i.CreatedAtUtc)).ToArray());
}
