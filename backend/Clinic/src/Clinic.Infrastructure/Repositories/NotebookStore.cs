using Clinic.Application.Notebook;
using Clinic.Domain.Entities;
using Clinic.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Clinic.Infrastructure.Repositories;

public sealed class NotebookStore(ClinicDbContext db, DbContextOptions<ClinicDbContext> options) : INotebookStore
{
    public Task<bool> PatientExistsAsync(Guid patientId, CancellationToken ct = default) =>
        db.Patients.AnyAsync(x => x.Id == patientId, ct);

    public Task<bool> DoctorExistsAsync(Guid doctorId, CancellationToken ct = default) =>
        db.Doctors.AnyAsync(x => x.Id == doctorId, ct);

    public Task<Guid?> VisitPatientIdAsync(Guid visitId, CancellationToken ct = default) =>
        db.Set<Visit>().AsNoTracking().Where(x => x.Id == visitId)
            .Select(x => (Guid?)x.PatientId).SingleOrDefaultAsync(ct);

    public Task<NotebookPage?> GetPageAsync(Guid patientId, Guid pageId, CancellationToken ct = default) =>
        db.Set<NotebookPage>().AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == pageId && x.PatientId == patientId, ct);

    // The requested page is projected plus one lookahead row for hasMore; the skip math uses
    // the caller's page size, so the extra row only extends this page's window.
    public async Task<IReadOnlyList<NotebookPageListItem>> ListPagesAsync(Guid patientId, int page, int pageSize, CancellationToken ct = default) =>
        await db.Set<NotebookPage>().AsNoTracking().Where(x => x.PatientId == patientId)
            .OrderByDescending(x => x.CreatedAtUtc).ThenByDescending(x => x.Id)
            .Skip((page - 1) * pageSize).Take(pageSize + 1)
            .Select(x => new NotebookPageListItem(x.Id, x.PatientId, x.VisitId, x.AuthorDoctorId, x.Title,
                x.CreatedAtUtc, x.UpdatedAtUtc, x.CurrentRevisionNumber))
            .ToArrayAsync(ct);

    public void Add(NotebookPage page)
    {
        db.Add(page);
        foreach (var collection in db.Entry(page).Collections) collection.IsLoaded = true;
    }

    public async Task SaveAsync(CancellationToken ct = default)
    {
        try { await db.SaveChangesAsync(ct); }
        catch { DiscardChanges(); throw; }
    }

    public void DiscardChanges()
    {
        foreach (var entry in db.ChangeTracker.Entries()
            .Where(x => x.Entity is NotebookPage or NotebookRevision or StoredFile).ToArray())
            entry.State = EntityState.Detached;
    }

    public async Task<INotebookRevisionTransaction> BeginRevisionAsync(Guid patientId, Guid pageId, CancellationToken ct)
    {
        var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            // Existing transactional UPDLOCK convention: serialize only this aggregate, across hosts.
            var page = await db.Set<NotebookPage>().FromSqlInterpolated(
                $"SELECT * FROM [NotebookPages] WITH (UPDLOCK, ROWLOCK) WHERE [Id] = {pageId} AND [PatientId] = {patientId}")
                .SingleOrDefaultAsync(ct);
            return new RevisionTransaction(db, tx, page);
        }
        catch { await tx.DisposeAsync(); throw; }
    }

    private sealed class RevisionTransaction(ClinicDbContext db,
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction tx, NotebookPage? page) : INotebookRevisionTransaction
    {
        public NotebookPage? Page => page;
        public async Task CommitAsync(CancellationToken ct)
        {
            await tx.CommitAsync(ct);
            db.ChangeTracker.AcceptAllChanges();
        }
        public ValueTask DisposeAsync() => tx.DisposeAsync(); // Uncommitted transaction rolls back.
    }

    public Task<NotebookRevisionReference?> DraftAsync(Guid pageId, string clientDraftId, CancellationToken ct) =>
        References(db.Set<NotebookRevision>().Where(x => x.PageId == pageId && x.ClientDraftId == clientDraftId)).SingleOrDefaultAsync(ct);

    public Task<NotebookRevisionReference?> PayloadAsync(Guid patientId, Guid pageId, long revisionNumber, CancellationToken ct) =>
        References(from revision in db.Set<NotebookRevision>()
                   join page in db.Set<NotebookPage>() on revision.PageId equals page.Id
                   where page.PatientId == patientId && page.Id == pageId && revision.RevisionNumber == revisionNumber
                   select revision).SingleOrDefaultAsync(ct);

    private IQueryable<NotebookRevisionReference> References(IQueryable<NotebookRevision> revisions) =>
        from revision in revisions.AsNoTracking()
        join file in db.Set<StoredFile>().AsNoTracking() on revision.StoredFileId equals file.Id into files
        from file in files.DefaultIfEmpty()
        select new NotebookRevisionReference(revision, file);

    public void AddPayload(StoredFile file, NotebookRevision revision, NotebookPage page, byte[] expectedVersion)
    {
        db.Add(file);
        db.Add(revision);
        ExpectVersion(page, expectedVersion);
    }

    public void ExpectVersion(NotebookPage page, byte[] expectedVersion)
    {
        db.Entry(page).Property(x => x.RowVersion).OriginalValue = expectedVersion.ToArray();
        db.Entry(page).Property(x => x.UpdatedAtUtc).IsModified = true;
    }

    public async Task SaveRevisionAsync(CancellationToken ct) => await db.SaveChangesAsync(false, ct);

    public async Task<bool> RevisionCommittedAsync(Guid revisionId, CancellationToken ct)
    {
        await using var verify = new ClinicDbContext(options);
        return await verify.Set<NotebookRevision>().AsNoTracking().AnyAsync(x => x.Id == revisionId, ct);
    }
}
