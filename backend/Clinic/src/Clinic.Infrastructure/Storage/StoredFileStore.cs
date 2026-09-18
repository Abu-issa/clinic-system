using Clinic.Application.Storage;
using Clinic.Domain.Entities;
using Clinic.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Clinic.Infrastructure.Storage;

public sealed class StoredFileStore(ClinicDbContext db) : IStoredFileStore
{
    public async Task SaveAsync(StoredFile file, CancellationToken cancellationToken = default)
    {
        db.Add(file);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            // A rejected save must not leak tracked metadata into a later unrelated save.
            db.Entry(file).State = EntityState.Detached;
            throw;
        }
    }
}
