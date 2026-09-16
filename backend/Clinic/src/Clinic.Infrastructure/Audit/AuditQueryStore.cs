using Clinic.Application.Audit;
using Clinic.Domain.Entities;
using Clinic.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Clinic.Infrastructure.Audit;

public sealed class AuditQueryStore(ClinicDbContext db) : IAuditQueryStore
{
    public async Task<AuditQueryResult> QueryAsync(AuditQuery query, IReadOnlyCollection<Guid> patientScopes,
        bool includeAdministrative, CancellationToken ct)
    {
        // Enforce bounds here too: direct application callers must not create unbounded queries.
        if (query.Page is < 1 or > 1000 || query.PageSize is < 1 or > 100 ||
            query.ToUtc <= query.FromUtc || query.ToUtc - query.FromUtc > TimeSpan.FromDays(31))
            throw new ArgumentException("Invalid audit query bounds.");
        var scopes = patientScopes.ToArray();
        var source = db.Set<AuditEvent>().AsNoTracking().Where(x =>
            x.OccurredAtUtc >= query.FromUtc && x.OccurredAtUtc < query.ToUtc &&
            (x.PatientId.HasValue ? scopes.Contains(x.PatientId.Value) : includeAdministrative));
        if (query.PatientId is { } patientId) source = source.Where(x => x.PatientId == patientId);
        if (query.ActorStaffId is { } actor) source = source.Where(x => x.ActorStaffId == actor);
        if (query.ActionCode is { } action) source = source.Where(x => x.ActionCode == action);
        if (query.ResourceType is { } type) source = source.Where(x => x.ResourceType == type);
        if (query.ResourceId is { } id) source = source.Where(x => x.ResourceId == id);
        if (query.Outcome is { } outcome) source = source.Where(x => x.Outcome == outcome);
        var rows = await source.OrderByDescending(x => x.OccurredAtUtc).ThenByDescending(x => x.Id)
            .Skip((query.Page - 1) * query.PageSize).Take(query.PageSize + 1)
            .Select(x => new AuditEventDto(x.Id, x.OccurredAtUtc, x.ActorStaffId, x.ActionCode,
                x.ResourceType, x.ResourceId, x.PatientId, x.Outcome, x.TraceId, x.Metadata)).ToListAsync(ct);
        return new(rows.Take(query.PageSize).ToArray(), query.Page, query.PageSize, rows.Count > query.PageSize);
    }
}
