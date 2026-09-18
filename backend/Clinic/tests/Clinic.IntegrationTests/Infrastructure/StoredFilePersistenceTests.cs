using System.Data.Common;
using Clinic.Application.Storage;
using Clinic.Domain.Entities;
using Clinic.Infrastructure.Persistence;
using Clinic.Infrastructure.Storage;
using Clinic.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;
using Xunit;

namespace Clinic.IntegrationTests.Infrastructure;

public sealed class StoredFilePersistenceTests(SqlDatabaseFixture database) : IClassFixture<SqlDatabaseFixture>, IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ClinicTests_files_{Guid.NewGuid():N}");

    private static readonly DateTimeOffset Now = new(2026, 9, 17, 8, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.MinValue;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private LocalFileStorage Provider() => new(Options.Create(
        new FileStorageOptions { Provider = "Local", LocalRoot = _root, MaxFileSizeBytes = 1_000_000 }));

    private StoredFileService Service(ClinicDbContext db, TimeProvider? clock = null)
    {
        var fixedClock = new FixedClock { Now = Now };
        return new StoredFileService(Provider(), new StoredFileStore(db), clock ?? fixedClock);
    }

    private static Stream Bytes(byte[] bytes) => new MemoryStream(bytes);

    [Fact]
    public async Task StoredFileMetadataRoundTripsEveryField()
    {
        var key = FileStorageKey.NewStorageKey();
        var file = new StoredFile(key, "round-trip.pdf", "application/pdf", 1234,
            new string('a', 64), "staff-42", Now);
        await using (var db = database.CreateContext())
        {
            db.Add(file);
            await db.SaveChangesAsync();
        }

        await using var verify = database.CreateContext();
        var saved = await verify.Set<StoredFile>().AsNoTracking().SingleAsync(x => x.StorageKey == key);
        Assert.Equal(file.Id, saved.Id);
        Assert.Equal(key, saved.StorageKey);
        Assert.Equal("round-trip.pdf", saved.OriginalFileName);
        Assert.Equal("application/pdf", saved.ContentType);
        Assert.Equal(1234, saved.SizeBytes);
        Assert.Equal(new string('a', 64), saved.Sha256);
        Assert.Equal(Now, saved.CreatedAtUtc);
        Assert.Equal("staff-42", saved.CreatedByStaffId);
    }

    [Fact]
    public async Task NullableActorAndUniqueStorageKeyConstraintsHold()
    {
        var key = FileStorageKey.NewStorageKey();
        await using (var db = database.CreateContext())
        {
            db.Add(new StoredFile(key, "system-generated.bin",
                "application/octet-stream", 5, new string('b', 64), null, Now));
            await db.SaveChangesAsync();
        }

        await using var verify = database.CreateContext();
        var saved = await verify.Set<StoredFile>().AsNoTracking().SingleAsync(x => x.StorageKey == key);
        Assert.Null(saved.CreatedByStaffId);

        // Duplicate StorageKey is impossible: the provider generates fresh keys and the unique
        // index is the database backstop for any direct privileged writer.
        await using var offending = database.CreateContext();
        offending.Add(new StoredFile(key, "again.bin", "application/octet-stream", 5,
            new string('c', 64), null, Now));
        var rejected = await Assert.ThrowsAsync<DbUpdateException>(() => offending.SaveChangesAsync());
        Assert.IsType<Microsoft.Data.SqlClient.SqlException>(rejected.InnerException);
    }

    [Fact]
    public async Task StoredFileFoundationHasNoForeignKeysOrCascadeCoupling()
    {
        await using var db = database.CreateContext();
        var entity = db.Model.FindEntityType(typeof(StoredFile))!;
        Assert.Equal("StoredFiles", entity.GetTableName());
        Assert.Empty(entity.GetForeignKeys());
        // The foundation is reusable metadata: no Patient/Visit/Prescription/Audit coupling.
        Assert.DoesNotContain(entity.GetProperties(), p => p.Name.Contains("Patient", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StoringThroughTheServicePersistsMetadataAndObject()
    {
        var bytes = "integration synthetic bytes"u8.ToArray();
        Guid id;
        string key;
        await using (var db = database.CreateContext())
        {
            var result = await Service(db).StoreAsync(new(Bytes(bytes), "visit-note.pdf",
                "application/pdf", "staff-7"));
            id = result.Id;
            key = result.StorageKey;
        }

        await using var verify = database.CreateContext();
        var saved = await verify.Set<StoredFile>().AsNoTracking().SingleAsync(x => x.Id == id);
        Assert.Equal(key, saved.StorageKey);
        Assert.Equal(bytes.LongLength, saved.SizeBytes);
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant(),
            saved.Sha256);
        Assert.Equal(Now, saved.CreatedAtUtc);
        var provider = Provider();
        Assert.True(await provider.ExistsAsync(key));
        await using var read = (await provider.OpenReadAsync(key))!;
        var roundTripped = new byte[bytes.Length];
        Assert.Equal(bytes.Length, await read.ReadAsync(roundTripped));
        Assert.Equal(bytes, roundTripped);
    }

    private sealed class FailStoredFileInsert : DbCommandInterceptor
    {
        private static void Check(DbCommand command)
        {
            if (command.CommandText.Contains("INSERT INTO [StoredFiles]", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("synthetic metadata outage");
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Check(command);
            return ValueTask.FromResult(result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Check(command);
            return ValueTask.FromResult(result);
        }
    }

    [Fact]
    public async Task MetadataSaveFailureLeavesNoFinalObject()
    {
        var factory = new SqlDbContextFactory(database, new FailStoredFileInsert());
        var bytes = "compensated bytes"u8.ToArray();
        await using var db = factory.CreateContext();
        var rejected = await Assert.ThrowsAsync<DbUpdateException>(() =>
            Service(db).StoreAsync(new(Bytes(bytes), "doomed.pdf", "application/pdf", "staff-7")));

        // EF wraps the injected outage; the compensation still ran.
        Assert.IsType<InvalidOperationException>(rejected.InnerException);
        Assert.Equal("synthetic metadata outage", rejected.InnerException!.Message);
        // No metadata row and no finalized object survived the failure.
        await using var verify = database.CreateContext();
        Assert.False(await verify.Set<StoredFile>().AnyAsync(x => x.Sha256 ==
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant()));
        Assert.False(Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories).Any());
    }

    private sealed class SqlDbContextFactory(SqlDatabaseFixture fixture, DbCommandInterceptor interceptor)
    {
        public ClinicDbContext CreateContext() => new(new DbContextOptionsBuilder<ClinicDbContext>()
            .UseSqlServer(fixture.ConnectionString)
            .AddInterceptors(interceptor)
            .Options);
    }

    // The integration fixture root is per-test-class and cleaned up with it.
    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
