using System.Text.Json;
using Clinic.Application.Audit;
using Clinic.Domain.Entities;
using Clinic.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Clinic.Infrastructure.Audit;

/// <summary>
/// Append-only audit persistence on the shared ClinicDbContext. Append stages an AuditEvent in
/// the caller's current unit of work, so Phase 2 mutation integrations can commit the event in
/// the same SQL transaction as the mutation (e.g. prescription finalize + audit append).
/// Save failures detach only the staged audit events: a co-committed mutation's state is left
/// for its own store's failure handling.
/// </summary>
public sealed class AuditEventStore(ClinicDbContext db, TimeProvider clock) : IAuditEventWriter
{
    private static readonly JsonSerializerOptions MetadataJson = new(JsonSerializerDefaults.Web);

    public static string SerializeMetadata(IReadOnlyDictionary<string, string> metadata) =>
        JsonSerializer.Serialize(metadata, MetadataJson);

    public static IReadOnlyDictionary<string, string> DeserializeMetadata(string json) =>
        JsonSerializer.Deserialize<IReadOnlyDictionary<string, string>>(json, MetadataJson)
        ?? new Dictionary<string, string>();

    public void Append(AuditAppendRequest request)
    {
        var auditEvent = new AuditEvent(
            request.ActorStaffId,
            request.ActionCode,
            request.ResourceType,
            request.ResourceId,
            request.PatientId,
            request.Outcome,
            request.TraceId,
            request.Metadata,
            clock.GetUtcNow());
        db.Add(auditEvent);
    }

    public async Task SaveAsync(CancellationToken ct = default)
    {
        try { await db.SaveChangesAsync(ct); }
        catch { DiscardStaged(); throw; }
    }

    private void DiscardStaged()
    {
        foreach (var entry in db.ChangeTracker.Entries<AuditEvent>()
                     .Where(e => e.State == EntityState.Added).ToArray())
            entry.State = EntityState.Detached;
    }
}
