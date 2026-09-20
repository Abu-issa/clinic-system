using Clinic.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Clinic.Infrastructure.Persistence;

internal static class NotebookMapping
{
    public static void Configure(ModelBuilder model)
    {
        var page = model.Entity<NotebookPage>();
        page.ToTable("NotebookPages", t => t.HasCheckConstraint(
            "CK_NotebookPages_RevisionNumber", "[CurrentRevisionNumber] >= 1"));
        page.HasKey(x => x.Id);
        page.Property(x => x.Id).ValueGeneratedNever();
        page.Property(x => x.Title).HasMaxLength(NotebookPage.MaxTitleLength).IsRequired();
        page.Property(x => x.RowVersion).IsRowVersion();
        // Clinical records are never cascade-deleted; Restrict everywhere matches existing tables.
        page.HasOne<Patient>().WithMany().HasForeignKey(x => x.PatientId).OnDelete(DeleteBehavior.Restrict);
        page.HasOne<Visit>().WithMany().HasForeignKey(x => x.VisitId).OnDelete(DeleteBehavior.Restrict);
        page.HasOne<Doctor>().WithMany().HasForeignKey(x => x.AuthorDoctorId).OnDelete(DeleteBehavior.Restrict);
        page.HasOne<Doctor>().WithMany().HasForeignKey(x => x.FinalizedByDoctorId).OnDelete(DeleteBehavior.Restrict);
        page.HasIndex(x => new { x.PatientId, x.CreatedAtUtc });
        page.HasIndex(x => new { x.PatientId, x.VisitId });

        var revision = model.Entity<NotebookRevision>();
        revision.ToTable("NotebookRevisions", t =>
        {
            t.HasCheckConstraint("CK_NotebookRevisions_RevisionNumber", "[RevisionNumber] >= 1");
            t.HasCheckConstraint("CK_NotebookRevisions_Payload", "([Kind] = 0 AND [StoredFileId] IS NULL) OR ([Kind] IN (1, 2) AND [StoredFileId] IS NOT NULL AND [ClientDraftId] IS NOT NULL AND [OriginDeviceId] IS NOT NULL)");
        });
        revision.HasKey(x => x.Id);
        revision.Property(x => x.Id).ValueGeneratedNever();
        revision.Property(x => x.Kind).HasConversion<int>();
        revision.HasOne<StoredFile>().WithMany().HasForeignKey(x => x.StoredFileId).OnDelete(DeleteBehavior.Restrict);
        revision.HasIndex(x => x.StoredFileId).IsUnique().HasFilter("[StoredFileId] IS NOT NULL");
        revision.Property(x => x.AuthorStaffId).HasMaxLength(450).IsRequired();
        revision.HasOne<Clinic.Infrastructure.Authentication.StaffUser>().WithMany()
            .HasForeignKey(x => x.AuthorStaffId).OnDelete(DeleteBehavior.Restrict);
        revision.Property(x => x.ClientDraftId).HasMaxLength(NotebookRevision.MaxClientDraftIdLength);
        revision.Property(x => x.OriginDeviceId).HasMaxLength(NotebookRevision.MaxOriginDeviceIdLength);
        revision.HasOne<NotebookPage>().WithMany(p => p.Revisions)
            .HasForeignKey(x => x.PageId).OnDelete(DeleteBehavior.Restrict);
        page.Navigation(x => x.Revisions).HasField("revisions")
            .UsePropertyAccessMode(PropertyAccessMode.Field);
        revision.HasIndex(x => new { x.PageId, x.RevisionNumber }).IsUnique();
        // Idempotency: a client draft ID identifies at most one revision per page. NULLs are
        // unlimited, so the unique index is filtered to real tokens.
        revision.HasIndex(x => new { x.PageId, x.ClientDraftId }).IsUnique()
            .HasFilter($"[{nameof(NotebookRevision.ClientDraftId)}] IS NOT NULL");
        revision.HasIndex(x => new { x.PageId, x.CreatedAtUtc });
    }
}
