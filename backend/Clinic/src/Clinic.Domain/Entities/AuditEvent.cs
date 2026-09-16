using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using Clinic.Domain.Enums;

namespace Clinic.Domain.Entities;

/// <summary>
/// One append-only audit record for a security or clinical operation. There is deliberately no
/// mutation API: an AuditEvent is fully constructed at creation and immutable afterwards, and
/// the DbContext rejects Modified/Deleted audit entries (see AuditMapping notes). Direct
/// privileged database writes remain outside the application guarantee.
/// </summary>
public sealed partial class AuditEvent
{
    public const int MaxActionCodeLength = 100;
    public const int MaxResourceTypeLength = 100;
    public const int MaxResourceIdLength = 256;
    public const int MaxActorStaffIdLength = 450;
    public const int MaxTraceIdLength = 128;
    public const int MaxMetadataEntries = 20;
    public const int MaxMetadataKeyLength = 64;
    public const int MaxMetadataValueLength = 256;
    /// <summary>
    /// Bounded total metadata text (raw key+value characters). The persisted JSON column is
    /// 4000 (SQL Server's non-max nvarchar parameter ceiling) because escaping can roughly
    /// double the serialized size; the raw bound keeps any valid event within the column.
    /// </summary>
    public const int MaxMetadataTotalLength = 2000;

    // Stable machine-readable codes: lowercase ASCII letters/digits with dot or hyphen
    // separators (e.g. "visit.finalize", "prescription.pdf.download", "staff-account").
    [GeneratedRegex(@"^[a-z0-9](?:[a-z0-9.-]{1,98})[a-z0-9]$")]
    private static partial Regex StableCode();

    // Metadata keys: lowercase machine-readable tokens (e.g. "reason-code", "grant.count").
    [GeneratedRegex(@"^[a-z0-9][a-z0-9._-]{0,63}$")]
    private static partial Regex MetadataKey();

    // Metadata values are machine-oriented tokens, not prose: codes, identifiers, counts,
    // flags, dates (e.g. "wrong-drug", "3", "true", "2026-09-16", "022da70a"). Free-form
    // sentences (which contain spaces or non-ASCII text) cannot be represented, so clinical
    // or sensitive text cannot be smuggled in under an innocuous key.
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._:/+-]{0,255}$")]
    private static partial Regex MetadataValue();

    // Defense-in-depth only: the primary protection is the narrow bounded string-only metadata
    // API itself. These key names are rejected outright because they would flag obvious secret
    // or credential material being smuggled into the audit trail.
    private static readonly string[] ForbiddenMetadataKeyFragments =
        ["password", "token", "secret", "cookie", "authorization", "api-key", "connectionstring", "connection-string", "credential", "otp", "totp", "recovery"];

    public Guid Id { get; private set; }

    /// <summary>Server-generated event time (TimeProvider-sourced), normalized to UTC.</summary>
    public DateTimeOffset OccurredAtUtc { get; private set; }

    /// <summary>ASP.NET Identity staff/account ID; null only for legitimate system events.</summary>
    public string? ActorStaffId { get; private set; }

    /// <summary>Stable machine-readable action, e.g. "prescription.finalize". Never clinical text.</summary>
    public string ActionCode { get; private set; } = string.Empty;

    /// <summary>Stable machine-readable resource kind, e.g. "prescription", "staff-account".</summary>
    public string ResourceType { get; private set; } = string.Empty;

    /// <summary>Opaque resource identifier as a string; future resources need not be GUIDs.</summary>
    public string ResourceId { get; private set; } = string.Empty;

    /// <summary>Present when the audited resource is patient-scoped and the patient is known.</summary>
    public Guid? PatientId { get; private set; }

    public AuditOutcome Outcome { get; private set; }

    /// <summary>HTTP Problem Details/request correlation identifier when available.</summary>
    public string? TraceId { get; private set; }

    /// <summary>Immutable bounded safe key/value metadata; empty when none. Never clinical payloads.</summary>
    public IReadOnlyDictionary<string, string> Metadata { get; private set; }
        = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>());

    private AuditEvent() { } // EF Core

    public AuditEvent(
        string? actorStaffId,
        string actionCode,
        string resourceType,
        string resourceId,
        Guid? patientId,
        AuditOutcome outcome,
        string? traceId,
        IEnumerable<KeyValuePair<string, string>>? metadata,
        DateTimeOffset occurredAtUtc)
    {
        if (actorStaffId is not null)
        {
            actorStaffId = actorStaffId.Trim();
            if (actorStaffId.Length == 0) actorStaffId = null;
            else if (actorStaffId.Length > MaxActorStaffIdLength)
                throw new ArgumentException($"Actor staff ID must not exceed {MaxActorStaffIdLength} characters.", nameof(actorStaffId));
        }
        ValidateCode(actionCode, MaxActionCodeLength, nameof(actionCode));
        ValidateCode(resourceType, MaxResourceTypeLength, nameof(resourceType));
        if (string.IsNullOrWhiteSpace(resourceId))
            throw new ArgumentException("Resource ID is required.", nameof(resourceId));
        resourceId = resourceId.Trim();
        if (resourceId.Length > MaxResourceIdLength)
            throw new ArgumentException($"Resource ID must not exceed {MaxResourceIdLength} characters.", nameof(resourceId));
        if (resourceId.Any(char.IsControl))
            throw new ArgumentException("Resource ID must not contain control characters.", nameof(resourceId));
        if (patientId == Guid.Empty)
            throw new ArgumentException("Patient ID must be a real identifier or null.", nameof(patientId));
        if (traceId is not null)
        {
            traceId = traceId.Trim();
            if (traceId.Length == 0) traceId = null;
            else if (traceId.Length > MaxTraceIdLength)
                throw new ArgumentException($"Trace ID must not exceed {MaxTraceIdLength} characters.", nameof(traceId));
            if (traceId is not null && traceId.Any(char.IsControl))
                throw new ArgumentException("Trace ID must not contain control characters.", nameof(traceId));
        }

        Id = Guid.NewGuid();
        ActorStaffId = actorStaffId;
        ActionCode = actionCode;
        ResourceType = resourceType;
        ResourceId = resourceId;
        PatientId = patientId;
        Outcome = outcome;
        TraceId = traceId;
        OccurredAtUtc = occurredAtUtc.ToUniversalTime();
        Metadata = CreateMetadata(metadata);
    }

    private static IReadOnlyDictionary<string, string> CreateMetadata(IEnumerable<KeyValuePair<string, string>>? metadata)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (metadata is null) return result;

        foreach (var entry in metadata)
        {
            var key = entry.Key?.Trim() ?? string.Empty;
            if (key.Length == 0)
                throw new ArgumentException("Metadata keys must not be blank.", nameof(metadata));
            if (!MetadataKey().IsMatch(key))
                throw new ArgumentException(
                    $"Metadata key '{key}' must be lowercase machine-readable tokens (letters, digits, dot, underscore, hyphen).", nameof(metadata));
            if (result.ContainsKey(key))
                throw new ArgumentException($"Duplicate metadata key '{key}' is rejected.", nameof(metadata));
            var value = entry.Value?.Trim() ?? string.Empty;
            if (value.Length == 0)
                throw new ArgumentException($"Metadata value for '{key}' must not be blank.", nameof(metadata));
            if (value.Length > MaxMetadataValueLength)
                throw new ArgumentException($"Metadata value for '{key}' must not exceed {MaxMetadataValueLength} characters.", nameof(metadata));
            if (!MetadataValue().IsMatch(value))
                throw new ArgumentException(
                    $"Metadata value for '{key}' must be a machine-safe token (letters, digits, dot, underscore, colon, slash, plus, hyphen) without spaces or prose.", nameof(metadata));
            if (ForbiddenMetadataKeyFragments.Any(fragment => key.Contains(fragment, StringComparison.Ordinal)))
                throw new ArgumentException(
                    $"Metadata key '{key}' matches credential/secret material and is never auditable.", nameof(metadata));
            result.Add(key, value);
        }

        if (result.Count > MaxMetadataEntries)
            throw new ArgumentException($"Metadata must not exceed {MaxMetadataEntries} entries.", nameof(metadata));
        if (result.Sum(entry => entry.Key.Length + entry.Value.Length) > MaxMetadataTotalLength)
            throw new ArgumentException($"Total metadata text must not exceed {MaxMetadataTotalLength} characters.", nameof(metadata));
        // Truly read-only: history cannot be mutated even by casting.
        return new ReadOnlyDictionary<string, string>(result);
    }

    private static void ValidateCode(string value, int maxLength, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"{name} is required.", name);
        value = value.Trim();
        if (value.Length > maxLength)
            throw new ArgumentException($"{name} must not exceed {maxLength} characters.", name);
        if (!StableCode().IsMatch(value))
            throw new ArgumentException(
                $"{name} must be stable machine-readable code: lowercase ASCII letters/digits with dot or hyphen separators.", name);
    }
}
