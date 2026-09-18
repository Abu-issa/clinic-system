using Clinic.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Clinic.Infrastructure.Persistence;

internal static class FileStorageMapping
{
    public static void Configure(ModelBuilder model)
    {
        var file = model.Entity<StoredFile>();
        file.ToTable("StoredFiles");
        file.HasKey(x => x.Id);
        file.Property(x => x.Id).ValueGeneratedNever();
        // Deliberately opaque: no foreign keys to StaffUser/Patient/clinical aggregates. The
        // foundation is reusable by every future attachment feature, and no cascade or
        // aggregate deletion can ever touch stored-file history.
        file.Property(x => x.StorageKey).HasMaxLength(StoredFile.MaxStorageKeyLength).IsRequired();
        file.Property(x => x.OriginalFileName).HasMaxLength(StoredFile.MaxOriginalFileNameLength).IsRequired();
        file.Property(x => x.ContentType).HasMaxLength(StoredFile.MaxContentTypeLength).IsRequired();
        file.Property(x => x.SizeBytes).IsRequired();
        file.Property(x => x.Sha256).HasMaxLength(64).IsRequired();
        file.Property(x => x.CreatedAtUtc).HasColumnType("datetimeoffset");
        file.Property(x => x.CreatedByStaffId).HasMaxLength(StoredFile.MaxCreatedByStaffIdLength);

        file.HasIndex(x => x.StorageKey).IsUnique();
        // Duplicate detection support and chronological administration; deliberately NOT unique
        // because identical bytes may legitimately represent different clinical records.
        file.HasIndex(x => x.Sha256);
        file.HasIndex(x => x.CreatedAtUtc);
    }
}
