namespace Clinic.Application.Audit;

/// <summary>Stages mutation events; the business unit of work owns persistence.</summary>
public interface IAuditMutationWriter
{
    void Append(AuditAppendRequest request);

    /// <summary>
    /// Owns only events appended during this operation. Disposal discards its unsaved events,
    /// preserving events staged before the operation and persisted append-only history.
    /// </summary>
    IDisposable BeginMutation();
}
