using Clinic.Domain.Enums;

namespace Clinic.Domain.Entities;

// Values supplied together for a draft edit; null means not recorded, never a clinical default.
public sealed record VisitClinicalContent(string? ChiefComplaint = null, string? Symptoms = null,
    string? Diagnosis = null, string? ClinicianNotes = null, string? InternalNotes = null,
    string? PatientSummary = null, DateTimeOffset? SuggestedFollowUpAtUtc = null);

public sealed class Visit
{
    private readonly List<VisitAmendment> amendments = [];
    private readonly List<VitalMeasurement> vitalMeasurements = [];
    private Visit() { }

    public Visit(Guid patientId, Guid doctorId, Guid? appointmentId, DateTimeOffset occurredAtUtc,
        string actor, DateTimeOffset now)
    {
        if (patientId == Guid.Empty || doctorId == Guid.Empty || appointmentId == Guid.Empty)
            throw new ArgumentException("Valid encounter identifiers are required.");
        Actor(actor);
        Id = Guid.NewGuid(); PatientId = patientId; DoctorId = doctorId; AppointmentId = appointmentId;
        OccurredAtUtc = occurredAtUtc.ToUniversalTime();
        CreatedAtUtc = LastModifiedAtUtc = now.ToUniversalTime();
        CreatedByStaffId = LastModifiedByStaffId = actor;
    }

    public Guid Id { get; private set; }
    public Guid PatientId { get; private set; }
    public Guid DoctorId { get; private set; }
    public Guid? AppointmentId { get; private set; }
    public DateTimeOffset OccurredAtUtc { get; private set; }
    public string? ChiefComplaint { get; private set; }
    public string? Symptoms { get; private set; }
    public string? Diagnosis { get; private set; }
    public string? ClinicianNotes { get; private set; }
    public string? InternalNotes { get; private set; }
    public string? PatientSummary { get; private set; }
    public DateTimeOffset? SuggestedFollowUpAtUtc { get; private set; }
    public VisitStatus Status { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public string CreatedByStaffId { get; private set; } = null!;
    public DateTimeOffset LastModifiedAtUtc { get; private set; }
    public string LastModifiedByStaffId { get; private set; } = null!;
    public DateTimeOffset? FinalizedAtUtc { get; private set; }
    public string? FinalizedByStaffId { get; private set; }
    public byte[] RowVersion { get; private set; } = [];
    public IReadOnlyCollection<VisitAmendment> Amendments => amendments.AsReadOnly();
    public IReadOnlyCollection<VitalMeasurement> VitalMeasurements => vitalMeasurements.AsReadOnly();

    public void UpdateDraft(VisitClinicalContent content, string actor, DateTimeOffset now)
    {
        RequireDraft(); Actor(actor); ArgumentNullException.ThrowIfNull(content);
        // Validate all fields before changing the aggregate.
        var values = new[] { content.ChiefComplaint, content.Symptoms, content.Diagnosis,
            content.ClinicianNotes, content.InternalNotes, content.PatientSummary }.Select(Text).ToArray();
        ChiefComplaint = values[0]; Symptoms = values[1]; Diagnosis = values[2];
        ClinicianNotes = values[3]; InternalNotes = values[4]; PatientSummary = values[5];
        SuggestedFollowUpAtUtc = content.SuggestedFollowUpAtUtc?.ToUniversalTime();
        Touch(actor, now);
    }

    public void FinalizeVisit(string actor, DateTimeOffset now)
    {
        RequireDraft(); Actor(actor);
        Status = VisitStatus.Finalized; FinalizedByStaffId = actor; FinalizedAtUtc = now.ToUniversalTime();
        Touch(actor, now);
    }

    public void AddAmendment(string reason, string content, string actor, DateTimeOffset now)
    {
        if (Status != VisitStatus.Finalized) throw new InvalidOperationException("Only finalized visits accept amendments.");
        var amendment = new VisitAmendment(Id, reason, content, actor, now);
        amendments.Add(amendment); Touch(actor, now);
    }

    public void AddVital(VitalReading reading, DateTimeOffset measuredAtUtc, string actor, DateTimeOffset now)
    {
        RequireDraft();
        var measurement = new VitalMeasurement(Id, reading, measuredAtUtc, actor, now);
        vitalMeasurements.Add(measurement); Touch(actor, now);
    }

    private void RequireDraft()
    {
        if (Status != VisitStatus.Draft) throw new InvalidOperationException("Finalized clinical content is immutable.");
    }
    private void Touch(string actor, DateTimeOffset now) { LastModifiedByStaffId = actor; LastModifiedAtUtc = now.ToUniversalTime(); }
    internal static void Actor(string actor)
    {
        if (string.IsNullOrWhiteSpace(actor) || actor.Length > 450) throw new ArgumentException("A valid staff identifier is required.");
    }
    internal static string? Text(string? text)
    {
        if (text is null) return null;
        if (string.IsNullOrWhiteSpace(text) || text.Trim().Length > 8000) throw new ArgumentException("Clinical text must contain 1 to 8000 characters when supplied.");
        return text.Trim();
    }
}
