using Clinic.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Clinic.Infrastructure.Persistence;

internal static class PrescriptionMapping
{
    public static void Configure(ModelBuilder model)
    {
        var prescription = model.Entity<Prescription>();
        prescription.ToTable("Prescriptions", t =>
        {
            // Draft has no lifecycle audit columns; Finalized and Released accumulate them in order;
            // Cancelled is only reachable from Finalized/Released and always keeps its finalization audit.
            t.HasCheckConstraint("CK_Prescriptions_Lifecycle",
                "([Status] = 0 AND [FinalizedAtUtc] IS NULL AND [FinalizedByStaffId] IS NULL AND [ReleasedAtUtc] IS NULL AND [ReleasedByStaffId] IS NULL AND [CancelledAtUtc] IS NULL AND [CancelledByStaffId] IS NULL) OR " +
                "([Status] = 1 AND [FinalizedAtUtc] IS NOT NULL AND [FinalizedByStaffId] IS NOT NULL AND [ReleasedAtUtc] IS NULL AND [ReleasedByStaffId] IS NULL AND [CancelledAtUtc] IS NULL AND [CancelledByStaffId] IS NULL) OR " +
                "([Status] = 2 AND [FinalizedAtUtc] IS NOT NULL AND [FinalizedByStaffId] IS NOT NULL AND [ReleasedAtUtc] IS NOT NULL AND [ReleasedByStaffId] IS NOT NULL AND [CancelledAtUtc] IS NULL AND [CancelledByStaffId] IS NULL) OR " +
                "([Status] = 3 AND [FinalizedAtUtc] IS NOT NULL AND [FinalizedByStaffId] IS NOT NULL AND [CancelledAtUtc] IS NOT NULL AND [CancelledByStaffId] IS NOT NULL)");
            // Only a cancelled prescription may carry the "replaced by" back-reference, matching
            // Prescription.SetReplacedBy; the forward link lives on the replacement itself.
            t.HasCheckConstraint("CK_Prescriptions_ReplacedByOnlyWhenCancelled",
                "[ReplacedByPrescriptionId] IS NULL OR [Status] = 3");
        });
        prescription.HasKey(x => x.Id);
        prescription.Property(x => x.Id).ValueGeneratedNever();
        prescription.Property(x => x.RowVersion).IsRowVersion();
        prescription.Property(x => x.Status).HasConversion<int>().IsRequired();
        prescription.Property(x => x.Notes).HasMaxLength(2000);
        prescription.Property(x => x.CancellationReason).HasMaxLength(1000);
        prescription.Property(x => x.FinalizedByStaffId).HasMaxLength(450);
        prescription.Property(x => x.ReleasedByStaffId).HasMaxLength(450);
        prescription.Property(x => x.CancelledByStaffId).HasMaxLength(450);
        prescription.Property(x => x.CreatedByStaffId).HasMaxLength(450).IsRequired();
        prescription.Property(x => x.LastModifiedByStaffId).HasMaxLength(450).IsRequired();
        foreach (var name in new[] { nameof(Prescription.CreatedAtUtc), nameof(Prescription.LastModifiedAtUtc),
                     nameof(Prescription.FinalizedAtUtc), nameof(Prescription.ReleasedAtUtc), nameof(Prescription.CancelledAtUtc) })
            prescription.Property(name).HasColumnType("datetimeoffset");

        prescription.HasOne<Visit>().WithMany().HasForeignKey(x => x.VisitId).OnDelete(DeleteBehavior.Restrict);
        prescription.HasOne<Patient>().WithMany().HasForeignKey(x => x.PatientId).OnDelete(DeleteBehavior.Restrict);
        prescription.HasOne<Doctor>().WithMany().HasForeignKey(x => x.DoctorId).OnDelete(DeleteBehavior.Restrict);
        prescription.HasOne<Prescription>().WithMany().HasForeignKey(x => x.ReplacesPrescriptionId).OnDelete(DeleteBehavior.Restrict);
        prescription.HasOne<Prescription>().WithMany().HasForeignKey(x => x.ReplacedByPrescriptionId).OnDelete(DeleteBehavior.Restrict);

        prescription.HasIndex(x => new { x.PatientId, x.CreatedAtUtc });
        prescription.HasIndex(x => x.VisitId);
        // The authoritative replacement direction. A filtered unique index on the forward link is
        // what enforces at most one replacement per cancelled original for direct writers too; the
        // original's rowversion only serializes cooperative service calls.
        prescription.HasIndex(x => x.ReplacesPrescriptionId)
            .IsUnique().HasFilter("[ReplacesPrescriptionId] IS NOT NULL");
        // Reverse-direction backstop: one cancelled original per replacement, so the two stored
        // directions stay 1:1 even under direct writes.
        prescription.HasIndex(x => x.ReplacedByPrescriptionId)
            .IsUnique().HasFilter("[ReplacedByPrescriptionId] IS NOT NULL");

        // Items are mutated only through the aggregate; severed draft items are deleted by EF
        // (ClientCascade) while the database foreign key itself stays NO ACTION like every other
        // relationship in this schema.
        prescription.HasMany(x => x.Items).WithOne().HasForeignKey(x => x.PrescriptionId)
            .OnDelete(DeleteBehavior.ClientCascade);
        prescription.Navigation(x => x.Items).HasField("_items").UsePropertyAccessMode(PropertyAccessMode.Field);

        var item = model.Entity<PrescriptionItem>();
        item.ToTable("PrescriptionItems");
        item.HasKey(x => x.Id);
        item.Property(x => x.Id).ValueGeneratedNever();
        item.Property(x => x.GenericNameEn).HasMaxLength(200);
        item.Property(x => x.GenericNameAr).HasMaxLength(200);
        item.Property(x => x.BrandNameEn).HasMaxLength(200);
        item.Property(x => x.BrandNameAr).HasMaxLength(200);
        item.Property(x => x.Strength).HasMaxLength(100).IsRequired();
        item.Property(x => x.Unit).HasMaxLength(50).IsRequired();
        item.Property(x => x.Form).HasConversion<int>();
        item.Property(x => x.Route).HasConversion<int>();
        // Dose, frequency and duration may be incomplete while the parent prescription is a Draft;
        // completeness is enforced by the aggregate at finalization, not by the column shape.
        item.Property(x => x.Dose).HasMaxLength(200);
        item.Property(x => x.Frequency).HasMaxLength(200);
        item.Property(x => x.Duration).HasMaxLength(200);
        item.Property(x => x.Instructions).HasMaxLength(1000);
        item.Property(x => x.CreatedByStaffId).HasMaxLength(450).IsRequired();
        item.Property(x => x.CreatedAtUtc).HasColumnType("datetimeoffset");
        item.HasOne<Medication>().WithMany().HasForeignKey(x => x.MedicationId).OnDelete(DeleteBehavior.Restrict);
        item.HasIndex(x => new { x.PrescriptionId, x.DisplayOrder });
    }
}
