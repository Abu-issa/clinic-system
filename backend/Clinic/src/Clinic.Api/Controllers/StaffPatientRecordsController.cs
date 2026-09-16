using Clinic.Application.Patients;
using Clinic.Api.Audit;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Clinic.Api.Controllers;

[ApiController]
[Route("api/staff/patients")]
[Authorize(Policy = "StaffSession")]
[RequestSizeLimit(131072)]
public sealed class StaffPatientRecordsController(PatientRecordsService service, IAuthorizationService authorization,
    IAntiforgery antiforgery, HttpAccessAudit audit) : ControllerBase
{
    [HttpPost]
    [Authorize(Policy = "PatientCreate")]
    public async Task<IResult> Create(CreatePatientRequest body, CancellationToken ct)
    {
        if (!await Csrf()) return Error(400, "invalid_csrf_token");
        return Admin(await service.CreateAsync(body, GetActor(), ct), created: true);
    }

    [HttpGet("{patientId:guid}")]
    public async Task<IResult> Get(Guid patientId, CancellationToken ct)
    {
        if (!await Allowed(patientId, "PatientAdminRead")) return Results.Forbid();
        var result = await service.GetAsync(patientId, ct);
        if (!result.IsSuccess) return Admin(result);
        return await audit.RecordAsync(HttpContext, "patient.record.read", "patient", patientId.ToString("N"), patientId)
            ?? Admin(result);
    }

    [HttpPut("{patientId:guid}")]
    public async Task<IResult> Update(Guid patientId, UpdatePatientRequest body, CancellationToken ct)
    {
        if (!await Allowed(patientId, "PatientAdminWrite")) return Results.Forbid();
        if (!await Csrf()) return Error(400, "invalid_csrf_token");
        return Admin(await service.UpdateAsync(patientId, body, GetActor(), ct));
    }

    [HttpGet("{patientId:guid}/medical-profile")]
    public async Task<IResult> GetProfile(Guid patientId, CancellationToken ct)
    {
        if (!await Allowed(patientId, "PatientClinicalRead")) return Results.Forbid();
        var result = await service.GetProfileAsync(patientId, ct);
        if (!result.IsSuccess) return Clinical(result);
        return await audit.RecordAsync(HttpContext, "patient.clinical-profile.read", "patient", patientId.ToString("N"), patientId)
            ?? Clinical(result);
    }

    [HttpPut("{patientId:guid}/medical-profile")]
    public async Task<IResult> SaveProfile(Guid patientId, SaveMedicalProfileRequest body, CancellationToken ct)
    {
        if (!await Allowed(patientId, "PatientClinicalWrite")) return Results.Forbid();
        if (!await Csrf()) return Error(400, "invalid_csrf_token");
        return Clinical(await service.SaveProfileAsync(patientId, body, GetActor() ?? "", ct));
    }

    private string? GetActor() =>
        User.FindFirst("staff_id")?.Value ?? User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;

    private async Task<bool> Allowed(Guid id, string policy) =>
        (await authorization.AuthorizeAsync(User, id, policy)).Succeeded;
    private async Task<bool> Csrf()
    {
        try { await antiforgery.ValidateRequestAsync(HttpContext); return true; }
        catch (AntiforgeryValidationException) { return false; }
    }
    private IResult Admin(PatientAdminResult result, bool created = false) => result.IsSuccess
        ? created ? Results.Created($"/api/staff/patients/{result.Details!.PatientId}", result.Details) : Results.Ok(result.Details)
        : Error(result.Error switch {
            PatientAdminError.PatientNotFound => 404,
            PatientAdminError.PatientChanged or PatientAdminError.MedicalRecordNumberAlreadyExists => 409,
            _ => 400 }, Code(result.Error.ToString()));
    private IResult Clinical(MedicalProfileResult result) => result.IsSuccess ? Results.Ok(result.Details)
        : Error(result.Error switch { MedicalProfileError.PatientNotFound => 404, MedicalProfileError.ProfileChanged => 409, _ => 400 }, Code(result.Error.ToString()));
    private IResult Error(int status, string code) => Results.Problem(statusCode: status,
        title: "The patient record request could not be completed.",
        extensions: new Dictionary<string, object?> { ["code"] = code, ["traceId"] = HttpContext.TraceIdentifier });
    private static string Code(string name) => System.Text.Json.JsonNamingPolicy.SnakeCaseLower.ConvertName(name);
}
