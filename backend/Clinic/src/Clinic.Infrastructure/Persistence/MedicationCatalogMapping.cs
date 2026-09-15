using Clinic.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Clinic.Infrastructure.Persistence;

internal static class MedicationCatalogMapping
{
    public static void Configure(ModelBuilder model)
    {
        var medication = model.Entity<Medication>();
        medication.ToTable("Medications", t =>
        {
            // Strength and unit are separate compact tokens (see Medication.ValidateStrengthAndUnit);
            // the SQL shape keeps the stored pair unambiguous even for direct writes.
            t.HasCheckConstraint("CK_Medications_StrengthToken", "CHARINDEX(' ', [Strength]) = 0");
            t.HasCheckConstraint("CK_Medications_UnitToken", "CHARINDEX(' ', [Unit]) = 0");
        });
        medication.HasKey(x => x.Id);
        medication.Property(x => x.Id).ValueGeneratedNever();
        medication.Property(x => x.RowVersion).IsRowVersion();
        medication.Property(x => x.GenericNameEn).HasMaxLength(200);
        medication.Property(x => x.GenericNameAr).HasMaxLength(200);
        medication.Property(x => x.BrandNameEn).HasMaxLength(200);
        medication.Property(x => x.BrandNameAr).HasMaxLength(200);
        medication.Property(x => x.Strength).HasMaxLength(100).IsRequired();
        medication.Property(x => x.Unit).HasMaxLength(50).IsRequired();
        medication.Property(x => x.Category).HasMaxLength(100);
        medication.Property(x => x.Form).HasConversion<int>();
        medication.Property(x => x.Route).HasConversion<int>();
        medication.Property(x => x.CreatedByStaffId).HasMaxLength(450).IsRequired();
        medication.Property(x => x.LastModifiedByStaffId).HasMaxLength(450).IsRequired();
        medication.Property(x => x.CreatedAtUtc).HasColumnType("datetimeoffset");
        medication.Property(x => x.LastModifiedAtUtc).HasColumnType("datetimeoffset");
        medication.HasIndex(x => x.IsActive);
        // Catalog identity is the generic name with strength, unit, form and route. The filtered
        // unique indexes back the application duplicate check against concurrent creates; inactive
        // entries keep their rows, so a withdrawn medication cannot be re-created as a duplicate.
        medication.HasIndex(x => new { x.GenericNameEn, x.Strength, x.Unit, x.Form, x.Route })
            .IsUnique().HasFilter("[GenericNameEn] IS NOT NULL");
        medication.HasIndex(x => new { x.GenericNameAr, x.Strength, x.Unit, x.Form, x.Route })
            .IsUnique().HasFilter("[GenericNameAr] IS NOT NULL");
    }
}
