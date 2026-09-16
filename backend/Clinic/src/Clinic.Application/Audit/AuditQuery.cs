using Clinic.Domain.Enums;

namespace Clinic.Application.Audit;

public sealed record AuditQuery(DateTimeOffset FromUtc, DateTimeOffset ToUtc, string? ActorStaffId,
    string? ActionCode, string? ResourceType, string? ResourceId, Guid? PatientId, AuditOutcome? Outcome,
    int Page, int PageSize);

public sealed record AuditEventDto(Guid Id, DateTimeOffset OccurredAtUtc, string? ActorStaffId,
    string ActionCode, string ResourceType, string ResourceId, Guid? PatientId, AuditOutcome Outcome,
    string? TraceId, IReadOnlyDictionary<string, string> Metadata);

public sealed record AuditQueryResult(IReadOnlyList<AuditEventDto> Items, int Page, int PageSize, bool HasMore);

public interface IAuditQueryStore
{
    Task<AuditQueryResult> QueryAsync(AuditQuery query, IReadOnlyCollection<Guid> patientScopes,
        bool includeAdministrative, CancellationToken ct);
}
