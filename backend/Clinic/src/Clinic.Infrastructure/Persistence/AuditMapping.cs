using System.Text.Json;
using Clinic.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Clinic.Infrastructure.Persistence;

internal static class AuditMapping
{
    public static void Configure(ModelBuilder model)
    {
        var audit = model.Entity<AuditEvent>();
        audit.ToTable("AuditEvents");
        audit.HasKey(x => x.Id);
        audit.Property(x => x.Id).ValueGeneratedNever();
        audit.Property(x => x.OccurredAtUtc).HasColumnType("datetimeoffset");
        // Deliberately opaque: no foreign keys to StaffUser/Patient/clinical aggregates, so
        // audit history stays readable even if source records are later removed, and no
        // cascade can ever delete audit history.
        audit.Property(x => x.ActorStaffId).HasMaxLength(450);
        audit.Property(x => x.ActionCode).HasMaxLength(AuditEvent.MaxActionCodeLength).IsRequired();
        audit.Property(x => x.ResourceType).HasMaxLength(AuditEvent.MaxResourceTypeLength).IsRequired();
        audit.Property(x => x.ResourceId).HasMaxLength(AuditEvent.MaxResourceIdLength).IsRequired();
        audit.Property(x => x.TraceId).HasMaxLength(AuditEvent.MaxTraceIdLength);
        audit.Property(x => x.Outcome).HasConversion<int>();

        // Bounded string-only metadata persisted as JSON. The domain bounds raw metadata text
        // to 2000 characters, so escaped JSON never exceeds the 4000-character column.
        audit.Property(x => x.Metadata)
            .HasColumnName("MetadataJson")
            .HasColumnType("nvarchar(4000)")
            .HasConversion(
                value => JsonSerializer.Serialize(value, AuditMetadataJson.Options),
                value => JsonSerializer.Deserialize<IReadOnlyDictionary<string, string>>(value, AuditMetadataJson.Options)
                         ?? new Dictionary<string, string>(),
                new ValueComparer<IReadOnlyDictionary<string, string>>(
                    (a, b) => JsonSerializer.Serialize(a, AuditMetadataJson.Options)
                              == JsonSerializer.Serialize(b, AuditMetadataJson.Options),
                    value => JsonSerializer.Serialize(value, AuditMetadataJson.Options).GetHashCode(),
                    value => new Dictionary<string, string>(value)));

        audit.HasIndex(x => x.OccurredAtUtc).IsDescending(false);
        audit.HasIndex(x => new { x.ActorStaffId, x.OccurredAtUtc }).IsDescending(false, false);
        audit.HasIndex(x => new { x.ResourceType, x.ResourceId, x.OccurredAtUtc }).IsDescending(false, false, false);
        audit.HasIndex(x => new { x.PatientId, x.OccurredAtUtc }).IsDescending(false, false);
        audit.HasIndex(x => new { x.ActionCode, x.OccurredAtUtc }).IsDescending(false, false);
    }
}

internal static class AuditMetadataJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}
