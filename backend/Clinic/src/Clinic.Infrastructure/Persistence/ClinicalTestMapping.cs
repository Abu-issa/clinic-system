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
            // Phase 2 must deliberately extend this constraint alongside real result transitions.
            table.HasCheckConstraint("CK_ClinicalTestRequests_Lifecycle", "[Status] = 0 AND [UploadedAtUtc] IS NULL AND [ReviewedAtUtc] IS NULL AND [ReviewedByDoctorId] IS NULL");
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
        // No global work-queue endpoints yet: status/category-only indexes are deferred.
    }
}
