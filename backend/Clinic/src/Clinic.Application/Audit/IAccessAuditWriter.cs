namespace Clinic.Application.Audit;

/// <summary>Durable standalone access event; never saves a caller's tracked business changes.</summary>
public interface IAccessAuditWriter
{
    Task WriteAsync(AuditAppendRequest request, CancellationToken ct = default);
}
