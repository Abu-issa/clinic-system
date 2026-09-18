using Clinic.Domain.Enums;

namespace Clinic.Domain.Entities;

// Historical order with explicit, one-way result/review commands.
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

    public void RecordResult(DateTimeOffset now)
    {
        if (Status == ClinicalTestStatus.Reviewed) throw new InvalidOperationException("Reviewed requests are immutable.");
        now = now.ToUniversalTime();
        if (now < RequestedAtUtc) throw new ArgumentException("Result time precedes request.");
        if (Status == ClinicalTestStatus.Requested)
        {
            Status = ClinicalTestStatus.Uploaded;
            UploadedAtUtc = now;
        }
    }

    public void StartReview()
    {
        if (Status != ClinicalTestStatus.Uploaded) throw new InvalidOperationException("Only uploaded requests can enter review.");
        Status = ClinicalTestStatus.UnderReview;
    }

    public void CompleteReview(Guid doctorId, DateTimeOffset now)
    {
        if (Status != ClinicalTestStatus.UnderReview) throw new InvalidOperationException("Only requests under review can complete review.");
        if (doctorId == Guid.Empty || now < UploadedAtUtc) throw new ArgumentException("Invalid reviewer or review time.");
        Status = ClinicalTestStatus.Reviewed;
        ReviewedByDoctorId = doctorId;
        ReviewedAtUtc = now.ToUniversalTime();
    }

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
