using Clinic.Api.Audit;
using Clinic.Application.Patients;
using Clinic.Infrastructure.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Clinic.Api.Controllers;

[ApiController]
[Route("api/mobile/staff/patients")]
[Authorize(Policy = "MobileStaffSession")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[RequestSizeLimit(4096)]
public sealed class MobilePatientContextController(PatientRecordsService patients, StaffAuthentication staff,
    IAuthorizationService authorization, HttpAccessAudit audit) : ControllerBase
{
    // POST keeps patient identifiers out of URL/access logs. This is a read-only operation.
    [HttpPost("search")]
    public async Task<IResult> Search(PatientContextSearchRequest body, CancellationToken ct)
    {
        var principal = await staff.PatientReadPrincipalAsync(User, ct);
        var scopes = principal.FindAll("patient_record_id")
            .Select(c => Guid.TryParse(c.Value, out var id) ? id : Guid.Empty)
            .Where(id => id != Guid.Empty).Distinct().ToArray();
        var allowed = new List<Guid>();
        foreach (var id in scopes)
            if ((await authorization.AuthorizeAsync(principal, id, "PatientClinicalRead")).Succeeded)
                allowed.Add(id);
        if (allowed.Count == 0) return Results.Forbid(authenticationSchemes: [MobileStaffAuthentication.Scheme]);
        var page = await patients.SearchContextAsync(body, allowed, ct);
        if (page is null) return Results.Problem(statusCode: 400, title: "Invalid patient search.",
            extensions: new Dictionary<string, object?> { ["code"] = "invalid_patient_search" });
        foreach (var item in page.Items)
        {
            var failure = await audit.RecordAsync(HttpContext, "patient.context.read", "patient",
                item.PatientId.ToString("N"), item.PatientId);
            if (failure is not null) return failure;
        }
        return Results.Ok(page);
    }
}
