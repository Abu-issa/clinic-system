using Clinic.Application.Attachments;
using Clinic.Domain.Entities;
using Clinic.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Clinic.Infrastructure.Repositories;

public sealed class AttachmentStore(ClinicDbContext db) : IAttachmentStore
{
    public Task<bool> PatientExistsAsync(Guid patientId, CancellationToken cancellationToken = default) =>
        db.Patients.AnyAsync(x => x.Id == patientId, cancellationToken);

    public Task<AttachmentVisitReference?> VisitAsync(Guid patientId, Guid visitId, CancellationToken cancellationToken = default) =>
        db.Set<Visit>().AsNoTracking().Where(x => x.Id == visitId && x.PatientId == patientId)
            .Select(x => new AttachmentVisitReference(x.Id, x.DoctorId))
            .SingleOrDefaultAsync(cancellationToken);

    public void Add(StoredFile file, PatientAttachment attachment)
    {
        db.Add(file);
        db.Add(attachment);
    }

    public Task<AttachmentDownloadReference?> DownloadAsync(Guid patientId, Guid attachmentId,
        CancellationToken cancellationToken = default) =>
        db.Set<PatientAttachment>().AsNoTracking()
            .Where(x => x.Id == attachmentId && x.PatientId == patientId)
            .Select(x => new AttachmentDownloadReference(x.Id, x.StoredFileId, x.File.StorageKey,
                x.File.OriginalFileName, x.File.ContentType))
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<AttachmentListItem>> ListAsync(Guid patientId, Guid? visitId, int page, int pageSize,
        CancellationToken cancellationToken = default) =>
        await db.Set<PatientAttachment>().AsNoTracking()
            .Where(x => x.PatientId == patientId && (visitId == null || x.VisitId == visitId))
            .OrderByDescending(x => x.CreatedAtUtc).ThenByDescending(x => x.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(x => new AttachmentListItem(x.Id, x.VisitId, x.File.OriginalFileName,
                x.File.ContentType, x.File.SizeBytes, x.CreatedAtUtc))
            .ToListAsync(cancellationToken);

    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            DiscardChanges();
            throw;
        }
    }

    public void DiscardChanges()
    {
        foreach (var entry in db.ChangeTracker.Entries()
                     .Where(x => x.Entity is StoredFile or PatientAttachment).ToArray())
            entry.State = EntityState.Detached;
    }
}
