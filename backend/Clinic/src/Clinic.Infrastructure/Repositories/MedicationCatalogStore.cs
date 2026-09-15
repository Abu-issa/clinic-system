using Clinic.Application.Medications;
using Clinic.Domain.Entities;
using Clinic.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Clinic.Infrastructure.Repositories;

public sealed class MedicationCatalogStore(ClinicDbContext db) : IMedicationCatalogStore
{
    public Task<Medication?> GetByIdAsync(Guid id, CancellationToken ct) =>
        db.Medications.SingleOrDefaultAsync(x => x.Id == id, ct);

    public async Task<(IReadOnlyList<MedicationListItem> Items, int TotalCount)> SearchAsync(
        MedicationSearchQuery query, CancellationToken ct)
    {
        var source = db.Medications.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(query.SearchTerm))
            source = source.Where(x =>
                (x.GenericNameEn != null && x.GenericNameEn.Contains(query.SearchTerm)) ||
                (x.GenericNameAr != null && x.GenericNameAr.Contains(query.SearchTerm)) ||
                (x.BrandNameEn != null && x.BrandNameEn.Contains(query.SearchTerm)) ||
                (x.BrandNameAr != null && x.BrandNameAr.Contains(query.SearchTerm)));
        if (query.ActiveOnly == true)
            source = source.Where(x => x.IsActive);

        var total = await source.CountAsync(ct);
        var items = await source.OrderBy(x => x.GenericNameEn).ThenBy(x => x.GenericNameAr).ThenBy(x => x.Id)
            .Skip(query.Skip).Take(query.Take)
            .Select(x => new MedicationListItem(
                x.Id, x.GenericNameEn, x.GenericNameAr, x.BrandNameEn, x.BrandNameAr,
                x.Strength, x.Unit, x.Form, x.Route, x.Category, x.IsActive))
            .ToArrayAsync(ct);
        return (items, total);
    }

    public Task<bool> ExistsAsync(Guid id, CancellationToken ct) => db.Medications.AnyAsync(x => x.Id == id, ct);

    public Task<bool> ExistsDuplicateAsync(Medication candidate, CancellationToken ct) => db.Medications.AsNoTracking()
        .AnyAsync(x =>
            x.Strength == candidate.Strength && x.Unit == candidate.Unit &&
            x.Form == candidate.Form && x.Route == candidate.Route &&
            ((candidate.GenericNameEn != null && x.GenericNameEn == candidate.GenericNameEn) ||
             (candidate.GenericNameAr != null && x.GenericNameAr == candidate.GenericNameAr)), ct);

    public void Add(Medication medication) => db.Medications.Add(medication);

    public void ExpectVersion(Medication medication, byte[] version)
    {
        db.Entry(medication).Property(x => x.RowVersion).OriginalValue = version.ToArray();
        // Force an aggregate update even when a fixed clock produces identical modification times.
        db.Entry(medication).Property(x => x.LastModifiedAtUtc).IsModified = true;
    }

    public async Task SaveAsync(CancellationToken ct)
    {
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 })
        {
            DiscardChanges();
            throw new MedicationCatalogConflictException();
        }
        catch { DiscardChanges(); throw; }
    }

    public void DiscardChanges()
    {
        // A rejected command must not leave pending catalog mutations available to a later save.
        foreach (var entry in db.ChangeTracker.Entries().Where(x => x.Entity is Medication).ToArray())
            entry.State = EntityState.Detached;
    }
}
