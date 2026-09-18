using System.ComponentModel.DataAnnotations;
using Clinic.Application.ClinicalTests;
using Clinic.Domain.Enums;

namespace Clinic.Api.StaffMvc;

public sealed class CreateStaffTestForm
{
    [Required] public ClinicalTestCategory? Category { get; set; }
    [Required, StringLength(200)] public string? TestName { get; set; }
    public Guid? VisitId { get; set; }
    [StringLength(2000)] public string? ClinicalInstructions { get; set; }
}
public sealed record StaffTestListPage(ClinicalTestPatientContext Patient, IReadOnlyList<ClinicalTestSummary> Items,
    int Page, int PageSize, ClinicalTestCategory? Category, ClinicalTestStatus? Status, bool CanCreate);
public sealed record StaffTestCreatePage(ClinicalTestPatientContext Patient, IReadOnlyList<ClinicalTestVisitChoice> Visits);
public sealed record StaffTestDetailPage(ClinicalTestPatientContext Patient, ClinicalTestDetails Test,
    IReadOnlyList<ClinicalTestAttachmentDetails>? Attachments, bool CanUpload, bool CanReview, long MaxFileSizeBytes);
