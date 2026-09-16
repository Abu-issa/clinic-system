using Clinic.Application.Audit;
using Clinic.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Clinic.Infrastructure.Audit;

public sealed class AccessAuditWriter(DbContextOptions<ClinicDbContext> options, TimeProvider clock) : IAccessAuditWriter
{
    public async Task WriteAsync(AuditAppendRequest request, CancellationToken ct = default)
    {
        await using var db = new ClinicDbContext(options);
        var writer = new AuditEventStore(db, clock);
        writer.Append(request);
        await writer.SaveAsync(ct);
    }
}
