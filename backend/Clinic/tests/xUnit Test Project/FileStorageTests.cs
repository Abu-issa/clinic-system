using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Clinic.Application.Storage;
using Clinic.Domain.Entities;
using Clinic.Infrastructure.Storage;
using Microsoft.Extensions.Options;

namespace Clinic.UnitTests;

public sealed class FileStorageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ClinicUnit_files_{Guid.NewGuid():N}");
    private readonly FixedClock _clock = new() { Now = new DateTimeOffset(2026, 9, 17, 9, 0, 0, TimeSpan.Zero) };

    private sealed class FixedClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; }
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private LocalFileStorage Provider(long maxBytes = FileStorageOptions.DefaultMaxFileSizeBytes)
    {
        var options = new FileStorageOptions { Provider = "Local", LocalRoot = _root, MaxFileSizeBytes = maxBytes };
        return new LocalFileStorage(Options.Create(options));
    }

    private RecordingStore Store(bool failOnSave = false) => new(failOnSave);

    /// <summary>In-memory stand-in for IStoredFileStore that records call order.</summary>
    private sealed class RecordingStore(bool failOnSave) : IStoredFileStore
    {
        public List<StoredFile> Saved { get; } = [];
        public List<string> Calls { get; } = [];

        public async Task SaveAsync(StoredFile file, CancellationToken cancellationToken = default)
        {
            Calls.Add("save");
            if (failOnSave) throw new InvalidOperationException("synthetic db outage");
            await Task.Yield();
            Saved.Add(file);
        }
    }

    private static Stream Bytes(string text) => new MemoryStream(Encoding.UTF8.GetBytes(text));

    private static string ExpectedSha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private StoredFileService Service(IFileStorage provider, RecordingStore store) =>
        new(provider, store, _clock);

    [Fact]
    public async Task StoringPersistsExactBytesWithMeasuredFacts()
    {
        var provider = Provider();
        var store = Store();
        var content = "synthetic clinical attachment bytes — no real PHI";
        var bytes = Encoding.UTF8.GetBytes(content);

        var result = await Service(provider, store).StoreAsync(
            new(Bytes(content), "scan.pdf", "application/pdf", "staff-1"));

        Assert.StartsWith(FileStorageKey.Prefix, result.StorageKey);
        Assert.Matches(@"^clinic-files/[0-9a-f]{32}$", result.StorageKey);
        Assert.DoesNotContain("scan.pdf", result.StorageKey);
        Assert.Equal(bytes.LongLength, result.SizeBytes);
        Assert.Equal(ExpectedSha256(bytes), result.Sha256);
        Assert.Equal(_clock.Now, result.CreatedAtUtc);
        Assert.Equal("staff-1", result.CreatedByStaffId);

        await using var stored = (await provider.OpenReadAsync(result.StorageKey))!;
        using var reader = new StreamReader(stored);
        Assert.Equal(content, await reader.ReadToEndAsync());
        Assert.True(await provider.ExistsAsync(result.StorageKey));
        Assert.Single(store.Saved);
    }

    [Fact]
    public async Task HostileOriginalFileNamesNeverAffectTheKeyOrLocation()
    {
        var provider = Provider();
        var hostile = new[] { "../../escape.pdf", "C:\\Windows\\evil.dll", "\\\\server\\share\\x.bin", "..\\..\\x" };
        var keys = new List<string>();
        foreach (var name in hostile)
        {
            var result = await Service(provider, Store()).StoreAsync(new(Bytes("x"), name, "application/octet-stream", null));
            keys.Add(result.StorageKey);
            Assert.True(await provider.ExistsAsync(result.StorageKey));
            Assert.Equal(name.Trim(), result.OriginalFileName);
        }
        Assert.Equal(hostile.Length, keys.Distinct().Count());
        // Nothing escaped the private root: the staging directory contains only the key tree.
        Assert.True(Directory.Exists(Path.Combine(_root, "clinic-files")));
    }

    [Fact]
    public async Task DuplicateOriginalFileNamesGetSeparateObjects()
    {
        var provider = Provider();
        var service = Service(provider, Store());
        var first = await service.StoreAsync(new(Bytes("one"), "same-name.pdf", "application/pdf", null));
        var second = await service.StoreAsync(new(Bytes("two"), "same-name.pdf", "application/pdf", null));
        Assert.NotEqual(first.StorageKey, second.StorageKey);
        Assert.NotEqual(first.Sha256, second.Sha256);
        Assert.True(await provider.ExistsAsync(first.StorageKey));
        Assert.True(await provider.ExistsAsync(second.StorageKey));
    }

    [Fact]
    public async Task ZeroBytePayloadsAreRejectedWithoutPersistence()
    {
        var provider = Provider();
        var store = Store();
        await Assert.ThrowsAsync<EmptyFileException>(() =>
            Service(provider, store).StoreAsync(new(new MemoryStream(), "empty.pdf", "application/pdf", null)));
        Assert.Empty(store.Saved);
        Assert.False(Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories).Any());
    }

    [Fact]
    public async Task OversizedStreamsAreRejectedMidStreamAndCleanedUp()
    {
        var provider = Provider(maxBytes: 1024);
        var store = Store();
        // Undeclared oversized stream: more bytes than any declared length could excuse.
        var oversized = new MemoryStream(new byte[4096]);
        await Assert.ThrowsAsync<FileSizeLimitExceededException>(() =>
            Service(provider, store).StoreAsync(new(oversized, "big.bin", "application/octet-stream", null)));
        Assert.Empty(store.Saved);
        Assert.False(Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories).Any());
    }

    [Fact]
    public async Task PersistenceFailureDeletesThePromotedObjectAndCompensates()
    {
        var provider = Provider();
        var store = Store(failOnSave: true);
        var content = "orphan-free content";

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Service(provider, store).StoreAsync(new(Bytes(content), "file.pdf", "application/pdf", "staff-1")));

        // The single-row save committed nothing (no row-delete compensation can exist), and the
        // promoted object was removed: neither metadata nor bytes survive the failure.
        Assert.Equal(["save"], store.Calls);
        Assert.Empty(store.Saved);
        Assert.False(Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories).Any());
    }

    [Fact]
    public async Task StorageStageFailureLeavesNoMetadataAndNoStagedObject()
    {
        var provider = Provider(maxBytes: 4);
        var store = Store();
        var exploding = new ExplodingStream(failAfterBytes: 2);
        await Assert.ThrowsAsync<IOException>(() =>
            Service(provider, store).StoreAsync(new(exploding, "partial.bin", "application/octet-stream", null)));
        Assert.Empty(store.Calls);
        Assert.False(Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories).Any());
    }

    private sealed class ExplodingStream(int failAfterBytes) : Stream
    {
        private int _remaining = failAfterBytes;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (_remaining == 0) throw new IOException("synthetic read failure mid-stream");
            var take = Math.Min(count, _remaining);
            _remaining -= take;
            await Task.Yield();
            return take; // returns without writing meaningful data; zero-byte check not triggered
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task MissingObjectIsAControlledNullResult()
    {
        var provider = Provider();
        Assert.False(await provider.ExistsAsync($"clinic-files/{Guid.NewGuid():n}"));
        Assert.Null(await provider.OpenReadAsync($"clinic-files/{Guid.NewGuid():n}"));
    }

    [Fact]
    public async Task KeysCannotEscapeThePrivateRoot()
    {
        var provider = Provider();
        foreach (var hostile in new[]
                 {
                     "clinic-files/../../escape", "clinic-files/..\\escape", "clinic-files/evil:ads",
                     "../escape", "C:\\temp\\escape", "clinic-files/sub/inner", "other/{n}"
                 })
        {
            await Assert.ThrowsAnyAsync<ArgumentException>(() => provider.OpenReadAsync(hostile));
            await Assert.ThrowsAnyAsync<ArgumentException>(() => provider.ExistsAsync(hostile));
            await Assert.ThrowsAnyAsync<ArgumentException>(() => provider.DeleteAsync(hostile));
        }
    }

    [Fact]
    public async Task DeleteRemovesTheObjectAndToleratesMissingKeys()
    {
        var provider = Provider();
        var result = await Service(provider, Store()).StoreAsync(new(Bytes("x"), "f.bin", "application/octet-stream", null));
        Assert.True(await provider.ExistsAsync(result.StorageKey));
        await provider.DeleteAsync(result.StorageKey);
        Assert.False(await provider.ExistsAsync(result.StorageKey));
        await provider.DeleteAsync(result.StorageKey); // idempotent cleanup
    }

    [Fact]
    public void OptionsValidationRejectsUnsafeValues()
    {
        Assert.True(new FileStorageOptions().IsValid());
        Assert.False(new FileStorageOptions { Provider = "S3" }.IsValid());
        Assert.False(new FileStorageOptions { LocalRoot = "" }.IsValid());
        Assert.False(new FileStorageOptions { LocalRoot = "C:\"; drop" }.IsValid());
        Assert.False(new FileStorageOptions { MaxFileSizeBytes = 0 }.IsValid());
        Assert.False(new FileStorageOptions { MaxFileSizeBytes = FileStorageOptions.AbsoluteMaxFileSizeBytes + 1 }.IsValid());
    }

    [Fact]
    public async Task DirectoryJunctionBelowRootIsRefusedAndWritesNothingOutside()
    {
        if (!OperatingSystem.IsWindows()) return; // junctions are a Windows reparse-point concept
        var outside = Path.Combine(Path.GetTempPath(), $"ClinicUnit_outside_{Guid.NewGuid():N}");
        Directory.CreateDirectory(outside);
        var link = Path.Combine(_root, "clinic-files");
        try
        {
            Directory.CreateDirectory(_root); // the provider would create it lazily on first use
            using (var mklink = Process.Start(new ProcessStartInfo("cmd.exe",
                $"/c mklink /J \"{link}\" \"{outside}\"") { CreateNoWindow = true, UseShellExecute = false }))
            {
                Assert.NotNull(mklink);
                await mklink!.WaitForExitAsync();
                Assert.Equal(0, mklink.ExitCode);
            }

            var provider = Provider();
            var store = Store();
            // The staged path traverses the junction: refused before anything is created.
            await Assert.ThrowsAsync<IOException>(() =>
                Service(provider, store).StoreAsync(new(Bytes("x"), "f.bin", "application/octet-stream", null)));
            Assert.Empty(store.Saved);
            Assert.Empty(Directory.EnumerateFileSystemEntries(outside));
            await Assert.ThrowsAsync<IOException>(() => provider.OpenReadAsync($"clinic-files/{Guid.NewGuid():n}"));
            await Assert.ThrowsAsync<IOException>(() => provider.ExistsAsync($"clinic-files/{Guid.NewGuid():n}"));
        }
        finally
        {
            // Delete the junction itself (never its target) before the root cleanup.
            if (Directory.Exists(link)) Directory.Delete(link);
            if (Directory.Exists(outside)) Directory.Delete(outside);
        }
    }

    [Fact]
    public async Task CancellationMidStreamLeavesNoObjectAndNoMetadata()
    {
        var provider = Provider();
        var store = Store();
        using var cts = new CancellationTokenSource();
        var source = new CancelledAfterFirstRead(cts.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Service(provider, store).StoreAsync(new(source, "cancelled.bin", "application/octet-stream", null),
                cts.Token));
        // Original cancellation semantics preserved (not converted to another exception),
        // staged bytes cleaned, no final object, no metadata persisted.
        Assert.Empty(store.Saved);
        Assert.Empty(store.Calls);
        Assert.False(Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories).Any());
    }

    private sealed class CancelledAfterFirstRead(CancellationToken token) : Stream
    {
        private bool _readOnce;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (!_readOnce)
            {
                _readOnce = true;
                await Task.Yield();
                return Math.Min(count, 4); // some bytes first so the staged file exists
            }
            throw new OperationCanceledException(token);
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>Simulates a compensation outage: staging/promotion delegate through, but the
    /// compensating delete of a final key fails.</summary>
    private sealed class DeleteFailingStorage(IFileStorage inner) : IFileStorage
    {
        public Task<StagedObject> StageAsync(Stream content, CancellationToken cancellationToken = default) =>
            inner.StageAsync(content, cancellationToken);
        public Task PromoteAsync(string stagedKey, string storageKey, CancellationToken cancellationToken = default) =>
            inner.PromoteAsync(stagedKey, storageKey, cancellationToken);
        public Task<Stream?> OpenReadAsync(string storageKey, CancellationToken cancellationToken = default) =>
            inner.OpenReadAsync(storageKey, cancellationToken);
        public Task<bool> ExistsAsync(string storageKey, CancellationToken cancellationToken = default) =>
            inner.ExistsAsync(storageKey, cancellationToken);
        public Task DeleteAsync(string storageKey, CancellationToken cancellationToken = default) =>
            storageKey.Contains(FileStorageKey.StagingPrefix, StringComparison.Ordinal)
                ? inner.DeleteAsync(storageKey, cancellationToken)
                : Task.FromException(new IOException("synthetic compensation outage"));
    }

    [Fact]
    public async Task CompensationFailureKeepsOriginalFailureAndDocumentsTheOrphan()
    {
        var provider = new DeleteFailingStorage(Provider());
        var store = Store(failOnSave: true);
        var content = "orphan candidate";

        // The PRIMARY metadata failure propagates — the compensation failure is not masked.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Service(provider, store).StoreAsync(new(Bytes(content), "file.pdf", "application/pdf", "staff-1")));

        Assert.Empty(store.Saved);
        // Documented orphan: the promoted object outlives the failed request and no metadata
        // row references it. Reconciliation is a future operational concern, never a silent fix.
        var files = Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories).ToList();
        var file = Assert.Single(files);
        Assert.DoesNotContain("_staging", file);
        Assert.Equal(Encoding.UTF8.GetBytes(content), await File.ReadAllBytesAsync(file));
    }

    [Fact]
    public async Task PromoteNeverOverwritesAnExistingObject()
    {
        var provider = Provider();
        var key = FileStorageKey.NewStorageKey();
        var existing = new DirectoryInfo(Path.Combine(_root, "clinic-files"));
        existing.Create();
        var existingPath = Path.Combine(existing.FullName, key[FileStorageKey.Prefix.Length..]);
        var existingBytes = "irreplaceable existing bytes"u8.ToArray();
        await File.WriteAllBytesAsync(existingPath, existingBytes);

        var staged = await provider.StageAsync(Bytes("new bytes"), CancellationToken.None);
        await Assert.ThrowsAsync<IOException>(() =>
            provider.PromoteAsync(staged.StagedKey, key, CancellationToken.None));

        // The existing valid object is byte-for-byte unchanged; the collision is a controlled
        // IOException and the staged object is cleaned up without replacing anything.
        Assert.Equal(existingBytes, await File.ReadAllBytesAsync(existingPath));
        await provider.DeleteAsync(staged.StagedKey, CancellationToken.None);
        Assert.DoesNotContain(Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories),
            f => f.Contains("_staging", StringComparison.Ordinal));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
