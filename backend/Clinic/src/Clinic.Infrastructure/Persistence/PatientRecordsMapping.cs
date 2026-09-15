using Clinic.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Clinic.Infrastructure.Persistence;

internal static class PatientRecordsMapping
{
    public static void Configure(ModelBuilder model)
    {
        var patient = model.Entity<Patient>();
        patient.Property(x => x.MedicalRecordNumber).HasMaxLength(32);
        patient.HasIndex(x => x.MedicalRecordNumber).IsUnique().HasFilter("[MedicalRecordNumber] IS NOT NULL");
        patient.Property(x => x.LegacyPaperFileNumber).HasMaxLength(32);
        patient.Property(x => x.LegacyCoverImageReference).HasMaxLength(512);
        patient.Property(x => x.RowVersion).IsRowVersion();
        // Existing name/phone columns remain unbounded for legacy compatibility. Phone is not unique.
        patient.Property(x => x.EmergencyContactName).HasMaxLength(200);
        patient.Property(x => x.EmergencyContactPhone).HasMaxLength(50);
        patient.Property(x => x.EmergencyContactRelation).HasMaxLength(100);

        var profile = model.Entity<PatientMedicalProfile>();
        profile.ToTable("PatientMedicalProfiles", t => {
            t.HasCheckConstraint("CK_Profile_AllergyStatus", "[AllergyStatus] IN (0,1,2)");
            t.HasCheckConstraint("CK_Profile_BloodType", "[BloodType] IS NULL OR [BloodType] BETWEEN 1 AND 8");
            t.HasCheckConstraint("CK_Profile_SmokingStatus", "[SmokingStatus] IS NULL OR [SmokingStatus] BETWEEN 1 AND 3");
            t.HasCheckConstraint("CK_Profile_DiabetesType", "[DiabetesType] IS NULL OR [DiabetesType] BETWEEN 1 AND 4");
        });
        profile.HasKey(x => x.Id);
        profile.Property(x => x.Id).ValueGeneratedNever();
        profile.HasIndex(x => x.PatientId).IsUnique();
        profile.HasOne<Patient>().WithOne().HasForeignKey<PatientMedicalProfile>(x => x.PatientId).OnDelete(DeleteBehavior.Restrict);
        profile.Property(x => x.RowVersion).IsRowVersion();
        profile.Property(x => x.UpdatedByStaffId).HasMaxLength(450);

        Entry(model.Entity<PatientAllergy>(), "PatientAllergies");
        Entry(model.Entity<PatientChronicCondition>(), "PatientChronicConditions");
        Entry(model.Entity<PatientMedication>(), "PatientMedications");
        Entry(model.Entity<PatientSurgery>(), "PatientSurgeries");
        Entry(model.Entity<PatientFamilyHistoryEntry>(), "PatientFamilyHistoryEntries");
        profile.HasMany(x => x.Allergies).WithOne().HasForeignKey(x => x.ProfileId).OnDelete(DeleteBehavior.Restrict);
        profile.HasMany(x => x.ChronicConditions).WithOne().HasForeignKey(x => x.ProfileId).OnDelete(DeleteBehavior.Restrict);
        profile.HasMany(x => x.Medications).WithOne().HasForeignKey(x => x.ProfileId).OnDelete(DeleteBehavior.Restrict);
        profile.HasMany(x => x.Surgeries).WithOne().HasForeignKey(x => x.ProfileId).OnDelete(DeleteBehavior.Restrict);
        profile.HasMany(x => x.FamilyHistory).WithOne().HasForeignKey(x => x.ProfileId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<PatientAllergy>().Property(x => x.Substance).HasMaxLength(200);
        model.Entity<PatientAllergy>().Property(x => x.Reaction).HasMaxLength(500);
        model.Entity<PatientAllergy>().ToTable(t => t.HasCheckConstraint("CK_Allergy_Severity", "[Severity] IS NULL OR [Severity] IN (1,2,3)"));
        model.Entity<PatientChronicCondition>().Property(x => x.ConditionName).HasMaxLength(200);
        model.Entity<PatientChronicCondition>().Property(x => x.Notes).HasMaxLength(500);
        model.Entity<PatientMedication>().Property(x => x.MedicationName).HasMaxLength(200);
        model.Entity<PatientMedication>().ToTable(t => t.HasCheckConstraint("CK_Medication_Status", "[Status] IN (1,2)"));
        model.Entity<PatientSurgery>().Property(x => x.ProcedureName).HasMaxLength(200);
        model.Entity<PatientFamilyHistoryEntry>().Property(x => x.Relation).HasMaxLength(100);
        model.Entity<PatientFamilyHistoryEntry>().Property(x => x.Condition).HasMaxLength(200);
    }

    private static void Entry<T>(EntityTypeBuilder<T> entry, string table) where T : PatientClinicalEntry
    {
        // Independent concrete tables share domain behavior, not an EF inheritance hierarchy.
        entry.HasBaseType((Type?)null);
        entry.ToTable(table, t => {
            t.HasCheckConstraint("CK_" + table + "_Source", "[Source] IN (1,2)");
            t.HasCheckConstraint("CK_" + table + "_Review", "[ReviewStatus] IN (1,2)");
            t.HasCheckConstraint("CK_" + table + "_Verification", "([ReviewStatus] = 1 AND [VerifiedAtUtc] IS NULL AND [VerifiedByStaffId] IS NULL) OR ([ReviewStatus] = 2 AND [VerifiedAtUtc] IS NOT NULL AND [VerifiedByStaffId] IS NOT NULL)");
            t.HasCheckConstraint("CK_" + table + "_Supersession", "([SupersededAtUtc] IS NULL AND [SupersededByStaffId] IS NULL) OR ([SupersededAtUtc] IS NOT NULL AND [SupersededByStaffId] IS NOT NULL)");
        });
        entry.HasKey(x => x.Id);
        entry.Property(x => x.Id).ValueGeneratedNever();
        entry.Ignore(x => x.IsActive);
        entry.Property(x => x.RecordedByStaffId).HasMaxLength(450);
        entry.Property(x => x.VerifiedByStaffId).HasMaxLength(450);
        entry.Property(x => x.SupersededByStaffId).HasMaxLength(450);
        entry.HasIndex(x => new { x.ProfileId, x.SupersededAtUtc });
    }
}
