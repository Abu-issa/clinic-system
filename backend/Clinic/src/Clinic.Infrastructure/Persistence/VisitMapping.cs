using Clinic.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Clinic.Infrastructure.Persistence;

internal static class VisitMapping
{
    public static void Configure(ModelBuilder model)
    {
        var visit = model.Entity<Visit>();
        visit.ToTable("Visits", t => t.HasCheckConstraint("CK_Visits_Lifecycle",
            "([Status] = 0 AND [FinalizedAtUtc] IS NULL AND [FinalizedByStaffId] IS NULL) OR ([Status] = 1 AND [FinalizedAtUtc] IS NOT NULL AND [FinalizedByStaffId] IS NOT NULL)"));
        visit.HasKey(x => x.Id); visit.Property(x => x.Id).ValueGeneratedNever();
        visit.Property(x => x.RowVersion).IsRowVersion();
        visit.Property(x => x.Status).HasConversion<int>();
        foreach (var name in new[] { nameof(Visit.ChiefComplaint), nameof(Visit.Symptoms), nameof(Visit.Diagnosis),
                     nameof(Visit.ClinicianNotes), nameof(Visit.InternalNotes), nameof(Visit.PatientSummary) })
            visit.Property<string?>(name).HasMaxLength(8000);
        visit.Property(x => x.CreatedByStaffId).HasMaxLength(450).IsRequired();
        visit.Property(x => x.LastModifiedByStaffId).HasMaxLength(450).IsRequired();
        visit.Property(x => x.FinalizedByStaffId).HasMaxLength(450);
        visit.HasOne<Patient>().WithMany().HasForeignKey(x => x.PatientId).OnDelete(DeleteBehavior.Restrict);
        visit.HasOne<Doctor>().WithMany().HasForeignKey(x => x.DoctorId).OnDelete(DeleteBehavior.Restrict);
        visit.HasOne<Appointment>().WithMany().HasForeignKey(x => x.AppointmentId).OnDelete(DeleteBehavior.Restrict);
        visit.HasIndex(x => new { x.PatientId, x.OccurredAtUtc });
        visit.HasIndex(x => new { x.DoctorId, x.OccurredAtUtc });
        visit.HasMany(x => x.Amendments).WithOne().HasForeignKey(x => x.VisitId).OnDelete(DeleteBehavior.Restrict);
        visit.HasMany(x => x.VitalMeasurements).WithOne().HasForeignKey(x => x.VisitId).OnDelete(DeleteBehavior.Restrict);
        visit.Navigation(x => x.Amendments).HasField("amendments").UsePropertyAccessMode(PropertyAccessMode.Field);
        visit.Navigation(x => x.VitalMeasurements).HasField("vitalMeasurements").UsePropertyAccessMode(PropertyAccessMode.Field);

        var amendment = model.Entity<VisitAmendment>();
        amendment.ToTable("VisitAmendments"); amendment.HasKey(x => x.Id); amendment.Property(x => x.Id).ValueGeneratedNever();
        amendment.Property(x => x.Reason).HasMaxLength(8000).IsRequired();
        amendment.Property(x => x.AmendmentText).HasMaxLength(8000).IsRequired();
        amendment.Property(x => x.CreatedByStaffId).HasMaxLength(450).IsRequired();
        amendment.HasIndex(x => new { x.VisitId, x.CreatedAtUtc });

        var vital = model.Entity<VitalMeasurement>();
        // Each row has precisely the columns for its type, with a fixed unit. Explicit IS NOT NULL
        // matters: SQL CHECK accepts UNKNOWN, so numeric comparisons alone are insufficient.
        var columns = new[] { "SystolicMmHg", "DiastolicMmHg", "HeartRateBpm", "TemperatureCelsius", "OxygenSaturationPercent", "WeightKg", "HeightCm", "RespiratoryRateBreathsPerMin" };
        string Shape(int type, params string[] used) => $"([Type] = {type} AND [Unit] = {type} AND " +
            string.Join(" AND ", columns.Select(c => $"[{c}] IS {(used.Contains(c) ? "NOT " : "")}NULL")) + ")";
        vital.ToTable("VitalMeasurements", t =>
        {
            t.HasCheckConstraint("CK_VitalMeasurements_Shape", string.Join(" OR ",
                Shape(0, columns[0], columns[1]), Shape(1, columns[2]), Shape(2, columns[3]),
                Shape(3, columns[4]), Shape(4, columns[5]), Shape(5, columns[6]), Shape(6, columns[7])));
            t.HasCheckConstraint("CK_VitalMeasurements_Values", "([SystolicMmHg] IS NULL OR [SystolicMmHg] >= 0) AND ([DiastolicMmHg] IS NULL OR [DiastolicMmHg] >= 0) AND ([HeartRateBpm] IS NULL OR [HeartRateBpm] >= 0) AND ([OxygenSaturationPercent] IS NULL OR [OxygenSaturationPercent] BETWEEN 0 AND 100) AND ([WeightKg] IS NULL OR [WeightKg] >= 0) AND ([HeightCm] IS NULL OR [HeightCm] >= 0) AND ([RespiratoryRateBreathsPerMin] IS NULL OR [RespiratoryRateBreathsPerMin] > 0)");
        });
        vital.HasKey(x => x.Id); vital.Property(x => x.Id).ValueGeneratedNever();
        vital.Property(x => x.Type).HasConversion<int>(); vital.Property(x => x.Unit).HasConversion<int>();
        foreach (var c in columns.Skip(3).Take(4)) vital.Property<decimal?>(c).HasPrecision(10, 3);
        vital.Property(x => x.CreatedByStaffId).HasMaxLength(450).IsRequired();
        vital.HasIndex(x => new { x.VisitId, x.MeasuredAtUtc });
    }
}
