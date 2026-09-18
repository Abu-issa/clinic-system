using Clinic.Domain.Entities;
using Clinic.Domain.Enums;

namespace Clinic.Application.ClinicalTests;

public sealed record CreateClinicalTestRequest(Guid PatientId, Guid? VisitId,
    ClinicalTestCategory Category, string TestName, string? ClinicalInstructions);
public sealed record ClinicalTestSummary(Guid Id, Guid PatientId, Guid? VisitId,
    ClinicalTestCategory Category, string TestName, ClinicalTestStatus Status, DateTimeOffset RequestedAtUtc);
public sealed record ClinicalTestDetails(Guid Id, Guid PatientId, Guid? VisitId,
    ClinicalTestCategory Category, string TestName, string? ClinicalInstructions,
    ClinicalTestStatus Status, DateTimeOffset RequestedAtUtc);
public enum ClinicalTestError { None, InvalidInput, PatientNotFound, RequestNotFound, VisitNotFound, DoctorAuthority }
public sealed record ClinicalTestResult(ClinicalTestError Error, ClinicalTestDetails? Details = null);
public sealed record ClinicalTestListResult(ClinicalTestError Error, IReadOnlyList<ClinicalTestSummary> Items);

public interface IClinicalTestStore
{
    Task<bool> PatientExistsAsync(Guid patientId, CancellationToken ct);
    Task<Guid?> AssociatedDoctorAsync(string actorStaffId, CancellationToken ct);
    Task<Guid?> VisitDoctorAsync(Guid patientId, Guid visitId, CancellationToken ct);
    Task<ClinicalTestDetails?> GetAsync(Guid patientId, Guid requestId, CancellationToken ct);
    Task<IReadOnlyList<ClinicalTestSummary>> ListAsync(Guid patientId, ClinicalTestCategory? category,
        ClinicalTestStatus? status, int page, int pageSize, CancellationToken ct);
    void Add(ClinicalTestRequest request);
    Task SaveAsync(CancellationToken ct);
    void DiscardChanges();
}
