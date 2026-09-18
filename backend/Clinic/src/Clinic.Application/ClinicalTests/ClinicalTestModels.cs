using Clinic.Domain.Entities;
using Clinic.Domain.Enums;

namespace Clinic.Application.ClinicalTests;

public sealed record CreateClinicalTestRequest(Guid PatientId, Guid? VisitId,
    ClinicalTestCategory Category, string TestName, string? ClinicalInstructions);
public sealed record ClinicalTestSummary(Guid Id, Guid PatientId, Guid? VisitId,
    ClinicalTestCategory Category, string TestName, ClinicalTestStatus Status, DateTimeOffset RequestedAtUtc,
    DateTimeOffset? UploadedAtUtc = null, DateTimeOffset? ReviewedAtUtc = null, int ResultAttachmentCount = 0);
public sealed record ClinicalTestDetails(Guid Id, Guid PatientId, Guid? VisitId,
    ClinicalTestCategory Category, string TestName, string? ClinicalInstructions,
    ClinicalTestStatus Status, DateTimeOffset RequestedAtUtc,
    DateTimeOffset? UploadedAtUtc = null, DateTimeOffset? ReviewedAtUtc = null,
    byte[]? RowVersion = null, int ResultAttachmentCount = 0,
    IReadOnlyList<ClinicalTestAttachmentDetails>? ResultAttachments = null);
public sealed record ClinicalTestAttachmentDetails(Guid AttachmentId, string OriginalFileName,
    string ContentType, long SizeBytes, DateTimeOffset CreatedAtUtc);
public sealed record ClinicalTestPatientContext(Guid PatientId, string FullName, string? MedicalRecordNumber);
public sealed record ClinicalTestVisitChoice(Guid Id, DateTimeOffset OccurredAtUtc);
public enum ClinicalTestError { None, InvalidInput, PatientNotFound, RequestNotFound, VisitNotFound, DoctorAuthority, InvalidTransition, Conflict }
public sealed record ClinicalTestResult(ClinicalTestError Error, ClinicalTestDetails? Details = null);
public sealed record ClinicalTestListResult(ClinicalTestError Error, IReadOnlyList<ClinicalTestSummary> Items);

public interface IClinicalTestStore
{
    Task<bool> PatientExistsAsync(Guid patientId, CancellationToken ct);
    Task<ClinicalTestPatientContext?> PatientContextAsync(Guid patientId, CancellationToken ct);
    Task<IReadOnlyList<ClinicalTestVisitChoice>> VisitChoicesAsync(Guid patientId, Guid doctorId, CancellationToken ct);
    Task<Guid?> AssociatedDoctorAsync(string actorStaffId, CancellationToken ct);
    Task<Guid?> VisitDoctorAsync(Guid patientId, Guid visitId, CancellationToken ct);
    Task<ClinicalTestDetails?> GetAsync(Guid patientId, Guid requestId, CancellationToken ct);
    Task<ClinicalTestRequest?> LoadAsync(Guid patientId, Guid requestId, CancellationToken ct);
    Task<IReadOnlyList<ClinicalTestAttachmentDetails>> ResultsAsync(Guid patientId, Guid requestId, CancellationToken ct);
    void ExpectVersion(ClinicalTestRequest request, byte[] expected);
    void AddResult(ClinicalTestResultAttachment result);
    Task<IReadOnlyList<ClinicalTestSummary>> ListAsync(Guid patientId, ClinicalTestCategory? category,
        ClinicalTestStatus? status, int page, int pageSize, CancellationToken ct);
    void Add(ClinicalTestRequest request);
    Task SaveAsync(CancellationToken ct);
    void DiscardChanges();
}
