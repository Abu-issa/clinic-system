using Clinic.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Clinic.Infrastructure.Persistence;

internal static class ClinicalTestMapping
{
    public static void Configure(ModelBuilder model)
    {
        var request = model.Entity<ClinicalTestRequest>();
        request.ToTable("ClinicalTestRequests", table =>
        {
            table.HasCheckConstraint("CK_ClinicalTestRequests_Category", "[Category] IN (0, 1)");
            table.HasCheckConstraint("CK_ClinicalTestRequests_Lifecycle", "([Status] = 0 AND [UploadedAtUtc] IS NULL AND [ReviewedAtUtc] IS NULL AND [ReviewedByDoctorId] IS NULL) OR ([Status] IN (1, 2) AND [UploadedAtUtc] IS NOT NULL AND [RequestedAtUtc] <= [UploadedAtUtc] AND [ReviewedAtUtc] IS NULL AND [ReviewedByDoctorId] IS NULL) OR ([Status] = 3 AND [UploadedAtUtc] IS NOT NULL AND [ReviewedAtUtc] IS NOT NULL AND [ReviewedByDoctorId] IS NOT NULL AND [RequestedAtUtc] <= [UploadedAtUtc] AND [UploadedAtUtc] <= [ReviewedAtUtc])");
        });
        request.HasKey(x => x.Id);
        request.Property(x => x.Id).ValueGeneratedNever();
        request.Property(x => x.TestName).HasMaxLength(ClinicalTestRequest.MaxTestNameLength).IsRequired();
        request.Property(x => x.ClinicalInstructions).HasMaxLength(ClinicalTestRequest.MaxClinicalInstructionsLength);
        request.Property(x => x.Category).HasConversion<int>();
        request.Property(x => x.Status).HasConversion<int>();
        request.Property(x => x.RowVersion).IsRowVersion();
        request.HasOne<Patient>().WithMany().HasForeignKey(x => x.PatientId).OnDelete(DeleteBehavior.Restrict);
        request.HasOne<Visit>().WithMany().HasForeignKey(x => x.VisitId).OnDelete(DeleteBehavior.Restrict);
        request.HasOne<Doctor>().WithMany().HasForeignKey(x => x.RequestedByDoctorId).OnDelete(DeleteBehavior.Restrict);
        request.HasOne<Doctor>().WithMany().HasForeignKey(x => x.ReviewedByDoctorId).OnDelete(DeleteBehavior.Restrict);
        request.HasIndex(x => new { x.PatientId, x.RequestedAtUtc });
        request.HasIndex(x => new { x.VisitId, x.RequestedAtUtc });
        var result = model.Entity<ClinicalTestResultAttachment>();
        result.ToTable("ClinicalTestResultAttachments");
        result.HasKey(x => x.Id);
        result.Property(x => x.Id).ValueGeneratedNever();
        result.HasOne<ClinicalTestRequest>().WithMany().HasForeignKey(x => x.ClinicalTestRequestId).OnDelete(DeleteBehavior.Restrict);
        result.HasOne<PatientAttachment>().WithMany().HasForeignKey(x => x.PatientAttachmentId).OnDelete(DeleteBehavior.Restrict);
        result.HasIndex(x => x.PatientAttachmentId).IsUnique();
        result.HasIndex(x => new { x.ClinicalTestRequestId, x.LinkedAtUtc });
        // No global work-queue endpoints yet: status/category-only indexes are deferred.
    }
}
