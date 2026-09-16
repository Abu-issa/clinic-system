using Clinic.Api.Audit;
using Clinic.Application.Audit;
using Clinic.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Clinic.Api.Controllers;

[ApiController]
[Route("api/staff/audit-events")]
[Authorize(Policy = "AuditRead")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class StaffAuditEventsController(IAuditQueryStore store, HttpAccessAudit audit, TimeProvider clock) : ControllerBase
{
    [HttpGet]
    public async Task<IResult> Get(DateTimeOffset? fromUtc, DateTimeOffset? toUtc, string? actorStaffId,
        string? actionCode, string? resourceType, string? resourceId, Guid? patientId, AuditOutcome? outcome,
        int page = 1, int pageSize = 50, CancellationToken ct = default)
    {
        var to = (toUtc ?? clock.GetUtcNow()).ToUniversalTime();
        var from = (fromUtc ?? (to > DateTimeOffset.MinValue.AddDays(7) ? to.AddDays(-7) : DateTimeOffset.MinValue)).ToUniversalTime();
        if (page is < 1 or > 1000 || pageSize is < 1 or > 100 || to <= from || to - from > TimeSpan.FromDays(31) ||
            actorStaffId?.Length > 450 || actionCode?.Length > 100 || resourceType?.Length > 100 || resourceId?.Length > 256 ||
            patientId == Guid.Empty || outcome.HasValue && !Enum.IsDefined(outcome.Value))
            return Results.Problem(statusCode: 400, title: "Invalid audit query.",
                extensions: new Dictionary<string, object?> { ["code"] = "invalid_audit_query" });
        var scopes = User.HasClaim("permission", "audit.patient.read")
            ? User.FindAll("patient_record_id").Select(c => Guid.TryParse(c.Value, out var id) ? id : Guid.Empty)
                .Where(id => id != Guid.Empty).Distinct().ToArray() : [];
        if (patientId.HasValue && !scopes.Contains(patientId.Value)) return Results.Forbid();
        var result = await store.QueryAsync(new(from, to, actorStaffId, actionCode, resourceType, resourceId,
            patientId, outcome, page, pageSize), scopes, User.HasClaim("permission", "audit.admin.read"), ct);
        var failure = await audit.RecordAsync(HttpContext, "audit.query", "audit", "events", patientId);
        return failure ?? Results.Ok(result);
    }
}
