using Clinic.Domain.Enums;

namespace Clinic.Application.Patients;

public sealed record PatientContextSearchRequest(string? SearchTerm, int Page = 1, int PageSize = 10);
public sealed record PatientContextItem(Guid PatientId, string FullName, string? MedicalRecordNumber,
    DateOnly? DateOfBirth, AllergyStatus AllergyStatus);
public sealed record PatientContextPage(IReadOnlyList<PatientContextItem> Items, int Page, int PageSize, bool HasMore);
