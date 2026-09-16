using Clinic.Domain.Entities;
using Clinic.Domain.Enums;

namespace Clinic.Application.Audit;

/// <summary>
/// Validated input for one audit event. The Application contract is deliberately narrow:
/// only stable codes, opaque identifiers, and string-only bounded metadata are expressible, so
/// clinical payloads or sensitive objects cannot be serialized into the audit trail by
/// accident. See AuditEvent for the enforced bounds and exclusions.
/// </summary>
public sealed record AuditAppendRequest(
    string? ActorStaffId,
    string ActionCode,
    string ResourceType,
    string ResourceId,
    Guid? PatientId,
    AuditOutcome Outcome,
    string? TraceId,
    IReadOnlyCollection<KeyValuePair<string, string>>? Metadata);

/// <summary>
/// Append-only audit writer. There is deliberately no update/delete/replace surface: audit
/// history is immutable through the application. Append stages an event into the CURRENT unit
/// of work so a future mutation integration (e.g. prescription finalize + audit append) can
/// commit or roll back in the same SQL transaction; SaveAsync exists for standalone appends
/// that are not coupled to a mutation.
/// </summary>
public interface IAuditEventWriter
{
    /// <summary>
    /// Stages one event with a server-generated OccurredAtUtc (TimeProvider-sourced; callers
    /// never choose event time). Persistence happens with the surrounding unit of work.
    /// </summary>
    void Append(AuditAppendRequest request);

    /// <summary>Persists staged events for standalone (non-mutation-coupled) appends.</summary>
    Task SaveAsync(CancellationToken ct = default);
}
