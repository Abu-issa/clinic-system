using Clinic.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Clinic.Infrastructure.Persistence;

internal static class AttachmentMapping
{
    public static void Configure(ModelBuilder model)
    {
        var attachment = model.Entity<PatientAttachment>();
        attachment.ToTable("PatientAttachments");
        attachment.HasKey(x => x.Id);
        attachment.Property(x => x.Id).ValueGeneratedNever();
        attachment.Property(x => x.PatientId).IsRequired();
        attachment.Property(x => x.StoredFileId).IsRequired();
        attachment.Property(x => x.CreatedAtUtc).HasColumnType("datetimeoffset");
        attachment.Property(x => x.CreatedByStaffId)
            .HasMaxLength(PatientAttachment.MaxCreatedByStaffIdLength).IsRequired();

        // One physical StoredFile metadata row maps to exactly one clinical attachment link:
        // the same bytes can never become visible under two patients.
        attachment.HasIndex(x => x.StoredFileId).IsUnique();
        attachment.HasIndex(x => new { x.PatientId, x.CreatedAtUtc }).IsDescending(false, false);
        attachment.HasIndex(x => new { x.VisitId, x.CreatedAtUtc }).IsDescending(false, false);

        // Navigation for metadata projection in list/download queries. Restrictive
        // relationships only: deleting a Patient/Visit/StoredFile can never cascade clinical
        // attachment history away, and attachments are create-once (no delete surface).
        attachment.HasOne(x => x.File).WithMany()
            .HasForeignKey(x => x.StoredFileId).OnDelete(DeleteBehavior.Restrict);
        attachment.HasOne<Patient>().WithMany()
            .HasForeignKey(x => x.PatientId).OnDelete(DeleteBehavior.Restrict);
        attachment.HasOne<Visit>().WithMany()
            .HasForeignKey(x => x.VisitId).OnDelete(DeleteBehavior.Restrict);
    }
}
