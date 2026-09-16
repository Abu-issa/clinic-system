using System.Security.Claims;
using Clinic.Api.Audit;
using Clinic.Api.Visits;
using Clinic.Application.Visits;
using Clinic.Domain.Entities;
using Clinic.Infrastructure.Authentication;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace Clinic.Api.Controllers;

[ApiController]
[Route("api/staff/patients/{patientId:guid}/visits")]
[Authorize(Policy = "StaffSession")]
[RequestSizeLimit(131072)]
public sealed class StaffVisitsController(
    VisitService service,
    IAuthorizationService authorization,
    IAntiforgery antiforgery,
    UserManager<StaffUser> users, HttpAccessAudit audit) : ControllerBase
{
    [HttpPost]
    public async Task<IResult> Create(Guid patientId, [FromBody] CreateVisitBody body, CancellationToken ct)
    {
        if (!await Allowed(patientId, "VisitWrite")) return Results.Forbid();
        if (!await Csrf()) return CsrfError();
        if (body.DoctorId is null || body.DoctorId == Guid.Empty || body.OccurredAtUtc is null)
            return Error(400, "invalid_input");

        if (User.IsInRole("Doctor") && !await HasDoctorAuthorityAsync(body.DoctorId.Value, ct))
            return Results.Forbid();

        var actor = GetActor();
        if (actor is null) return Results.Unauthorized();

        var request = new CreateVisitRequest(patientId, body.DoctorId.Value, body.AppointmentId, body.OccurredAtUtc.Value);
        var result = await service.CreateAsync(request, actor, ct);
        return result.IsSuccess
            ? Results.Created($"/api/staff/patients/{patientId}/visits/{result.Details!.Id}", Map(result.Details))
            : MapError(result.Error);
    }

    [HttpGet]
    public async Task<IResult> List(Guid patientId, [FromQuery] int skip = 0, [FromQuery] int take = 50, CancellationToken ct = default)
    {
        if (!await Allowed(patientId, "VisitRead")) return Results.Forbid();
        var result = await service.ListAsync(patientId, skip, take, ct);
        if (!result.IsSuccess) return MapError(result.Error);
        var projection = result.Visits.Select(v => new VisitListItemResponse(v.Id, v.PatientId, v.DoctorId, v.AppointmentId, v.OccurredAtUtc, v.Status)).ToArray();
        return await audit.RecordAsync(HttpContext, "patient.visits.read", "patient", patientId.ToString("N"), patientId)
            ?? Results.Ok(projection);
    }

    [HttpGet("{visitId:guid}")]
    public async Task<IResult> Get(Guid patientId, Guid visitId, CancellationToken ct)
    {
        if (!await Allowed(patientId, "VisitRead")) return Results.Forbid();
        var result = await service.GetAsync(patientId, visitId, ct);
        if (!result.IsSuccess) return MapError(result.Error);
        var projection = Map(result.Details!);
        return await audit.RecordAsync(HttpContext, "visit.read", "visit", visitId.ToString("N"), patientId)
            ?? Results.Ok(projection);
    }

    [HttpPut("{visitId:guid}")]
    public async Task<IResult> Update(Guid patientId, Guid visitId, [FromBody] UpdateVisitBody body, CancellationToken ct)
    {
        if (!await Allowed(patientId, "VisitWrite")) return Results.Forbid();
        if (!await Csrf()) return CsrfError();
        if (body.ExpectedRowVersion is not { Length: 8 }) return Error(400, "invalid_row_version");

        var existing = await service.GetAsync(patientId, visitId, ct);
        if (!existing.IsSuccess) return MapError(existing.Error);
        if (User.IsInRole("Doctor") && !await HasDoctorAuthorityAsync(existing.Details!.DoctorId, ct))
            return Results.Forbid();

        var actor = GetActor();
        if (actor is null) return Results.Unauthorized();

        var content = new VisitClinicalContent(
            body.ChiefComplaint,
            body.Symptoms,
            body.Diagnosis,
            body.ClinicianNotes,
            body.InternalNotes,
            body.PatientSummary,
            body.SuggestedFollowUpAtUtc);

        var request = new UpdateVisitRequest(content, body.ExpectedRowVersion);
        var result = await service.UpdateAsync(patientId, visitId, request, actor, ct);
        return result.IsSuccess ? Results.Ok(Map(result.Details!)) : MapError(result.Error);
    }

    [HttpPost("{visitId:guid}/finalize")]
    public async Task<IResult> FinalizeVisit(Guid patientId, Guid visitId, [FromBody] FinalizeVisitBody body, CancellationToken ct)
    {
        if (!await Allowed(patientId, "VisitFinalize")) return Results.Forbid();
        if (!await Csrf()) return CsrfError();
        if (body.ExpectedRowVersion is not { Length: 8 }) return Error(400, "invalid_row_version");

        var existing = await service.GetAsync(patientId, visitId, ct);
        if (!existing.IsSuccess) return MapError(existing.Error);
        if (User.IsInRole("Doctor") && !await HasDoctorAuthorityAsync(existing.Details!.DoctorId, ct))
            return Results.Forbid();

        var actor = GetActor();
        if (actor is null) return Results.Unauthorized();

        var result = await service.FinalizeAsync(patientId, visitId, body.ExpectedRowVersion, actor, ct);
        return result.IsSuccess ? Results.Ok(Map(result.Details!)) : MapError(result.Error);
    }

    [HttpPost("{visitId:guid}/amendments")]
    public async Task<IResult> AddAmendment(Guid patientId, Guid visitId, [FromBody] AddAmendmentBody body, CancellationToken ct)
    {
        if (!await Allowed(patientId, "VisitAmend")) return Results.Forbid();
        if (!await Csrf()) return CsrfError();
        if (string.IsNullOrWhiteSpace(body.Reason) || string.IsNullOrWhiteSpace(body.AmendmentText) ||
            body.ExpectedRowVersion is not { Length: 8 })
            return Error(400, "invalid_input");

        var existing = await service.GetAsync(patientId, visitId, ct);
        if (!existing.IsSuccess) return MapError(existing.Error);
        if (User.IsInRole("Doctor") && !await HasDoctorAuthorityAsync(existing.Details!.DoctorId, ct))
            return Results.Forbid();

        var actor = GetActor();
        if (actor is null) return Results.Unauthorized();

        var request = new AddAmendmentRequest(body.Reason, body.AmendmentText, body.ExpectedRowVersion);
        var result = await service.AddAmendmentAsync(patientId, visitId, request, actor, ct);
        return result.IsSuccess ? Results.Ok(Map(result.Details!)) : MapError(result.Error);
    }

    [HttpPost("{visitId:guid}/vitals")]
    public async Task<IResult> AddVital(Guid patientId, Guid visitId, [FromBody] AddVitalBody body, CancellationToken ct)
    {
        if (!await Allowed(patientId, "VitalWrite")) return Results.Forbid();
        if (!await Csrf()) return CsrfError();
        if (body.MeasuredAtUtc is null || body.ExpectedRowVersion is not { Length: 8 } || body.Reading is null)
            return Error(400, "invalid_input");

        var reading = MapReading(body.Reading);
        if (reading is null) return Error(400, "invalid_input");

        var actor = GetActor();
        if (actor is null) return Results.Unauthorized();

        var request = new AddVitalRequest(reading, body.MeasuredAtUtc.Value, body.ExpectedRowVersion);
        var result = await service.AddVitalAsync(patientId, visitId, request, actor, ct);
        return result.IsSuccess ? Results.Ok(Map(result.Details!)) : MapError(result.Error);
    }

    private async Task<bool> Allowed(Guid patientId, string policy) =>
        (await authorization.AuthorizeAsync(User, patientId, policy)).Succeeded;

    private async Task<bool> HasDoctorAuthorityAsync(Guid doctorId, CancellationToken ct)
    {
        var staffId = GetActor();
        if (staffId is null) return false;
        var user = await users.FindByIdAsync(staffId);
        return user is not null && user.AssociatedDoctorId.HasValue && user.AssociatedDoctorId.Value == doctorId;
    }

    private async Task<bool> Csrf()
    {
        try { await antiforgery.ValidateRequestAsync(HttpContext); return true; }
        catch (AntiforgeryValidationException) { return false; }
    }

    private string? GetActor() =>
        User.FindFirst("staff_id")?.Value ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

    private static VitalReading? MapReading(VitalReadingBody body) => body switch
    {
        VitalReadingBody.BloodPressureBody bp when bp.Systolic.HasValue && bp.Diastolic.HasValue =>
            new VitalReading.BloodPressure(bp.Systolic.Value, bp.Diastolic.Value),
        VitalReadingBody.HeartRateBody hr when hr.Bpm.HasValue =>
            new VitalReading.HeartRate(hr.Bpm.Value),
        VitalReadingBody.TemperatureBody tp when tp.Celsius.HasValue =>
            new VitalReading.Temperature(tp.Celsius.Value),
        VitalReadingBody.OxygenSaturationBody ox when ox.Percent.HasValue =>
            new VitalReading.OxygenSaturation(ox.Percent.Value),
        VitalReadingBody.WeightBody wt when wt.Kg.HasValue =>
            new VitalReading.Weight(wt.Kg.Value),
        VitalReadingBody.HeightBody ht when ht.Cm.HasValue =>
            new VitalReading.Height(ht.Cm.Value),
        VitalReadingBody.RespiratoryRateBody rr when rr.BreathsPerMinute.HasValue =>
            new VitalReading.RespiratoryRate(rr.BreathsPerMinute.Value),
        _ => null
    };

    private IResult MapError(VisitError error) => error switch
    {
        VisitError.PatientNotFound => Error(404, "patient_not_found"),
        VisitError.DoctorNotFound => Error(404, "doctor_not_found"),
        VisitError.AppointmentNotFound => Error(404, "appointment_not_found"),
        VisitError.AppointmentMismatch => Error(409, "appointment_mismatch"),
        VisitError.VisitNotFound => Error(404, "visit_not_found"),
        VisitError.InvalidRowVersion => Error(400, "invalid_row_version"),
        VisitError.VisitChanged => Error(409, "visit_changed"),
        VisitError.InvalidLifecycle => Error(409, "invalid_lifecycle"),
        _ => Error(400, "invalid_input")
    };

    private IResult CsrfError() => Results.Problem(
        statusCode: 400,
        title: "The request verification token is invalid.",
        extensions: new Dictionary<string, object?> { ["code"] = "invalid_csrf_token", ["traceId"] = HttpContext.TraceIdentifier });

    private IResult Error(int status, string code) => Results.Problem(
        statusCode: status,
        title: "The visit request could not be completed.",
        extensions: new Dictionary<string, object?> { ["code"] = code, ["traceId"] = HttpContext.TraceIdentifier });

    private static VisitResponse Map(VisitDetails d) => new(
        d.Id,
        d.PatientId,
        d.DoctorId,
        d.AppointmentId,
        d.OccurredAtUtc,
        d.Status,
        d.ChiefComplaint,
        d.Symptoms,
        d.Diagnosis,
        d.ClinicianNotes,
        d.InternalNotes,
        d.PatientSummary,
        d.SuggestedFollowUpAtUtc,
        d.CreatedAtUtc,
        d.LastModifiedAtUtc,
        d.FinalizedAtUtc,
        d.RowVersion,
        d.Amendments.Select(a => new AmendmentResponse(a.Id, a.Reason, a.AmendmentText, a.CreatedAtUtc)).ToArray(),
        d.VitalMeasurements.Select(v => new VitalResponse(v.Id, v.Type, v.Unit, v.SystolicMmHg, v.DiastolicMmHg, v.HeartRateBpm,
            v.TemperatureCelsius, v.OxygenSaturationPercent, v.WeightKg, v.HeightCm, v.RespiratoryRateBreathsPerMin,
            v.MeasuredAtUtc, v.CreatedAtUtc)).ToArray());
}
