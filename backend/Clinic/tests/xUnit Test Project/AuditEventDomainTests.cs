using System.Collections;
using Clinic.Domain.Entities;
using Clinic.Domain.Enums;

namespace Clinic.UnitTests;

public sealed class AuditEventDomainTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);

    private static AuditEvent Create(
        string? actor = "staff-1",
        string action = "prescription.finalize",
        string resourceType = "prescription",
        string resourceId = "3f2a9c1e4b7d4e0f8a1b2c3d4e5f6a7b",
        Guid? patientId = null,
        AuditOutcome outcome = AuditOutcome.Succeeded,
        string? traceId = "trace-0HN7",
        IEnumerable<KeyValuePair<string, string>>? metadata = null,
        DateTimeOffset? occurredAt = null) =>
        new(actor, action, resourceType, resourceId, patientId, outcome, traceId, metadata, occurredAt ?? Now);

    [Fact]
    public void ValidEventCreationNormalizesTimestampToUtc()
    {
        var withOffset = new DateTimeOffset(2026, 9, 16, 15, 0, 0, TimeSpan.FromHours(3));
        var auditEvent = Create(occurredAt: withOffset);
        Assert.Equal(12, auditEvent.OccurredAtUtc.Hour);
        Assert.Equal(TimeSpan.Zero, auditEvent.OccurredAtUtc.Offset);
        Assert.NotEqual(Guid.Empty, auditEvent.Id);
    }

    [Fact]
    public void NullActorAndNullPatientAreAcceptedForSystemEvents()
    {
        var auditEvent = Create(actor: null, patientId: null);
        Assert.Null(auditEvent.ActorStaffId);
        Assert.Null(auditEvent.PatientId);
    }

    [Fact]
    public void BlankActorNormalizesToNullAndOversizedActorIsRejected()
    {
        Assert.Null(Create(actor: "  ").ActorStaffId);
        Assert.Throws<ArgumentException>(() => Create(actor: new string('s', AuditEvent.MaxActorStaffIdLength + 1)));
    }

    [Theory]
    [InlineData(AuditOutcome.Succeeded)]
    [InlineData(AuditOutcome.Failed)]
    public void BothOutcomesAreAccepted(AuditOutcome outcome)
    {
        Assert.Equal(outcome, Create(outcome: outcome).Outcome);
    }

    [Fact]
    public void ValidTraceIdRoundTripsAndBlankNormalizesToNull()
    {
        Assert.Equal("trace-0HN7", Create().TraceId);
        Assert.Null(Create(traceId: " ").TraceId);
        Assert.Throws<ArgumentException>(() => Create(traceId: new string('t', AuditEvent.MaxTraceIdLength + 1)));
        Assert.Throws<ArgumentException>(() => Create(traceId: "bad\u0001trace"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Has Space")]
    [InlineData("UPPER")]
    [InlineData("emoji-🙂")]
    [InlineData("a")]
    [InlineData(".leading")]
    [InlineData("trailing.")]
    public void InvalidActionCodesAreRejected(string actionCode)
    {
        Assert.Throws<ArgumentException>(() => Create(action: actionCode));
    }

    [Fact]
    public void OversizedActionCodeIsRejected()
    {
        Assert.Throws<ArgumentException>(() =>
            Create(action: new string('a', AuditEvent.MaxActionCodeLength - 1) + "x1"));
    }

    [Theory]
    [InlineData("visit.finalize")]
    [InlineData("prescription.pdf.download")]
    [InlineData("staff.grants.change")]
    [InlineData("patient.record.read")]
    [InlineData("a1.b2-c3")]
    public void ValidActionCodesAreAccepted(string actionCode)
    {
        Assert.Equal(actionCode, Create(action: actionCode).ActionCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("patient record")]
    [InlineData("Patient")]
    public void InvalidResourceTypesAreRejected(string resourceType)
    {
        Assert.Throws<ArgumentException>(() => Create(resourceType: resourceType));
    }

    [Fact]
    public void OversizedResourceTypeIsRejected()
    {
        Assert.Throws<ArgumentException>(() =>
            Create(resourceType: new string('r', AuditEvent.MaxResourceTypeLength + 1)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void BlankResourceIdsAreRejected(string resourceId)
    {
        Assert.Throws<ArgumentException>(() => Create(resourceId: resourceId));
    }

    [Fact]
    public void OversizedAndControlCharacterResourceIdsAreRejected()
    {
        Assert.Throws<ArgumentException>(() => Create(resourceId: new string('r', AuditEvent.MaxResourceIdLength + 1)));
        Assert.Throws<ArgumentException>(() => Create(resourceId: "id\u0000"));
    }

    [Fact]
    public void EmptyGuidPatientIdIsRejected()
    {
        Assert.Throws<ArgumentException>(() => Create(patientId: Guid.Empty));
    }

    [Fact]
    public void MetadataBoundariesAreAccepted()
    {
        var entries = Enumerable.Range(0, AuditEvent.MaxMetadataEntries)
            .Select(i => new KeyValuePair<string, string>($"key-{i}", new string('v', AuditEvent.MaxMetadataValueLength / AuditEvent.MaxMetadataEntries)))
            .ToList();
        entries[^1] = new(entries[^1].Key, new string('v', AuditEvent.MaxMetadataValueLength));
        // Keep total under the raw bound: use short values for all but the last.
        for (var i = 0; i < entries.Count - 1; i++)
            entries[i] = new(entries[i].Key, "v");

        var auditEvent = Create(metadata: entries);
        Assert.Equal(AuditEvent.MaxMetadataEntries, auditEvent.Metadata.Count);

        var maxKey = new string('k', AuditEvent.MaxMetadataKeyLength);
        Assert.Equal("value", Create(metadata: [new(maxKey, "value")]).Metadata[maxKey]);
        var maxValue = new string('v', AuditEvent.MaxMetadataValueLength);
        Assert.Equal(maxValue, Create(metadata: [new("max-value", maxValue)]).Metadata["max-value"]);
    }

    [Fact]
    public void MetadataOverTheBoundariesIsRejected()
    {
        var tooMany = Enumerable.Range(0, AuditEvent.MaxMetadataEntries + 1)
            .Select(i => new KeyValuePair<string, string>($"k{i}", "v"));
        Assert.Throws<ArgumentException>(() => Create(metadata: tooMany));

        Assert.Throws<ArgumentException>(() =>
            Create(metadata: [new(new string('k', AuditEvent.MaxMetadataKeyLength + 1), "v")]));
        Assert.Throws<ArgumentException>(() =>
            Create(metadata: [new("k", new string('v', AuditEvent.MaxMetadataValueLength + 1))]));
        Assert.Throws<ArgumentException>(() =>
            Create(metadata: [new("total", new string('v', AuditEvent.MaxMetadataTotalLength))]));
    }

    [Theory]
    [InlineData("Password")]
    [InlineData("reset-token")]
    [InlineData("authorization-header")]
    [InlineData("otp-code")]
    [InlineData("totp-secret")]
    [InlineData("recovery-codes")]
    [InlineData("session-cookie")]
    [InlineData("api-key")]
    [InlineData("connection-string")]
    public void CredentialLikeMetadataKeysAreRejected(string key)
    {
        Assert.Throws<ArgumentException>(() => Create(metadata: [new(key, "v")]));
    }

    [Fact]
    public void DuplicateMetadataKeysAreRejectedDeterministically()
    {
        Assert.Throws<ArgumentException>(() =>
            Create(metadata: [new("dup", "1"), new("dup", "2")]));
    }

    [Fact]
    public void MetadataIsImmutableAfterCreation()
    {
        var auditEvent = Create(metadata: [new("key", "value")]);
        // Mutation attempts throw, and the public surface exposes no setter or mutator method.
        // Mutation attempts fail outright: the read-only surface has no Add member at all.
        Assert.ThrowsAny<Exception>(() =>
        {
            dynamic mutable = auditEvent.Metadata;
            mutable.Add("other", "value");
        });
        // No publicly callable setter: the private setter exists only for EF materialization.
        Assert.All(typeof(AuditEvent).GetProperties(),
            p => Assert.True(p.SetMethod is null || !p.SetMethod.IsPublic, $"{p.Name} must not have a public setter."));
    }

    [Fact]
    public void NoUpdateOrDeleteOperationExistsOnTheEntityOrApplicationContract()
    {
        // Structural append-only: the entity declares no public methods at all (constructor +
        // read-only properties only), and the Application writer exposes only Append/SaveAsync.
        Assert.All(typeof(AuditEvent).GetMethods()
            .Where(m => m.IsPublic && !m.IsSpecialName && m.DeclaringType == typeof(AuditEvent)),
            m => Assert.Fail($"AuditEvent must not expose a public {m.Name} operation."));
        Assert.Equal(["Append", "SaveAsync"],
            typeof(Application.Audit.IAuditEventWriter).GetMethods().Select(m => m.Name).Order().ToList());
    }

    [Fact]
    public void MetadataValuesMustBeMachineSafeTokens()
    {
        // Option A (machine-safe value model): codes, identifiers, counts, flags, dates.
        var auditEvent = Create(metadata:
        [
            new("reason-code", "wrong-drug"),
            new("item.count", "3"),
            new("dispensed", "true"),
            new("finalized-on", "2026-09-16"),
            new("catalog.id", "022da70a"),
            new("ratio", "500/125"),
        ]);
        Assert.Equal("wrong-drug", auditEvent.Metadata["reason-code"]);
        Assert.Equal("500/125", auditEvent.Metadata["ratio"]);
    }

    [Theory]
    [InlineData("note", "patient diagnosed with mild pneumonia and prescribed rest")] // prose
    [InlineData("note", "مرضى اصطناعي")] // non-ASCII text
    [InlineData("note", "two words")] // spaces
    [InlineData("note", " leading space")]
    [InlineData("note", "control\u0001char")]
    [InlineData("note", "")]
    public void FreeFormClinicalTextIsNotRepresentableAsMetadata(string key, string value)
    {
        Assert.Throws<ArgumentException>(() => Create(metadata: [new(key, value)]));
    }

    [Fact]
    public void MetadataValuesAreTrimmedBeforeValidation()
    {
        // Trailing whitespace is normalized away, not stored: the value becomes a clean token.
        Assert.Equal("trailing", Create(metadata: [new("note", "trailing ")]).Metadata["note"]);
    }

    [Fact]
    public void ConstructorCopiesCallerMetadataSoLaterMutationCannotAffectTheEvent()
    {
        var callerMetadata = new Dictionary<string, string> { ["reason-code"] = "wrong-drug" };
        var auditEvent = Create(metadata: callerMetadata);

        callerMetadata["reason-code"] = "MUTATED";
        callerMetadata.Add("extra", "x");

        Assert.Equal("wrong-drug", auditEvent.Metadata["reason-code"]);
        Assert.Single(auditEvent.Metadata);
        Assert.False(auditEvent.Metadata.ContainsKey("extra"));
    }
}
