using Clinic.Domain.Enums;

namespace Clinic.Domain.Entities;

// Create-once order. Future result/review transitions require explicit domain methods;
// Phase 1 deliberately exposes no mutation, deletion, or attachment association.
public sealed class ClinicalTestRequest
{
    public const int MaxTestNameLength = 200;
    public const int MaxClinicalInstructionsLength = 2000;
    private ClinicalTestRequest() { }

    public ClinicalTestRequest(Guid patientId, Guid? visitId, Guid requestedByDoctorId,
        ClinicalTestCategory category, string testName, string? clinicalInstructions, DateTimeOffset now)
    {
        if (patientId == Guid.Empty || requestedByDoctorId == Guid.Empty || visitId == Guid.Empty)
            throw new ArgumentException("Valid clinical identifiers are required.");
        if (!Enum.IsDefined(category)) throw new ArgumentException("Invalid test category.");
        Id = Guid.NewGuid(); PatientId = patientId; VisitId = visitId;
        RequestedByDoctorId = requestedByDoctorId; Category = category;
        TestName = Text(testName, MaxTestNameLength, required: true)!;
        ClinicalInstructions = Text(clinicalInstructions, MaxClinicalInstructionsLength, required: false);
        Status = ClinicalTestStatus.Requested;
        RequestedAtUtc = now.ToUniversalTime();
    }

    public Guid Id { get; private set; }
    public Guid PatientId { get; private set; }
    public Guid? VisitId { get; private set; }
    public Guid RequestedByDoctorId { get; private set; }
    public ClinicalTestCategory Category { get; private set; }
    public string TestName { get; private set; } = null!;
    public string? ClinicalInstructions { get; private set; }
    public ClinicalTestStatus Status { get; private set; }
    public DateTimeOffset RequestedAtUtc { get; private set; }
    public DateTimeOffset? UploadedAtUtc { get; private set; }
    public DateTimeOffset? ReviewedAtUtc { get; private set; }
    public Guid? ReviewedByDoctorId { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    private static string? Text(string? value, int max, bool required)
    {
        if (value is not null && (value.Length > max || value.Any(char.IsControl)))
            throw new ArgumentException("Clinical text is invalid.");
        value = value?.Trim();
        if (!string.IsNullOrEmpty(value)) return value;
        if (required) throw new ArgumentException("Test name is required.");
        return null;
    }
}
