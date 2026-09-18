using System.Security.Cryptography;
using Clinic.Application.Storage;
using Microsoft.Extensions.Options;

namespace Clinic.Infrastructure.Storage;

/// <summary>
/// Private local-filesystem provider for development/test (and reviewed explicit production
/// opt-in). All objects live beneath one configured private root that is never wwwroot and is
/// never served by static files. Keys are validated and resolved to full paths that must remain
/// inside the root, so traversal, absolute paths, separator injection and Windows reserved-name
/// tricks cannot escape the store. Writes stage to a reserved sub-namespace on the same volume
/// and are promoted with an atomic move. Files are never rendered, executed or exposed via URLs.
/// </summary>
public sealed class LocalFileStorage(IOptions<FileStorageOptions> options) : IFileStorage
{
    private readonly FileStorageOptions _options = options.Value;
    private readonly object _rootLock = new();
    private string? _resolvedRoot;

    public async Task<StagedObject> StageAsync(Stream content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        var root = EnsureRoot();
        var stagedKey = $"{FileStorageKey.StagingPrefix}{Guid.NewGuid():n}";
        var path = ResolvePath(stagedKey);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            await using var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = await content.ReadAsync(buffer, cancellationToken)) > 0)
            {
                total += read;
                if (total > _options.MaxFileSizeBytes)
                    throw new FileSizeLimitExceededException(_options.MaxFileSizeBytes);
                hasher.AppendData(buffer, 0, read);
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
            if (total == 0) throw new EmptyFileException();
            await target.FlushAsync(cancellationToken);
            return new(stagedKey, Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant(), total);
        }
        catch
        {
            try { File.Delete(path); } catch (FileNotFoundException) { } catch (DirectoryNotFoundException) { }
            throw;
        }
    }

    public Task PromoteAsync(string stagedKey, string storageKey, CancellationToken cancellationToken = default)
    {
        ValidateKey(stagedKey, FileStorageKey.StagingPrefix);
        ValidateKey(storageKey, FileStorageKey.Prefix);
        var root = EnsureRoot();
        var source = ResolvePath(stagedKey);
        var destination = ResolvePath(storageKey);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        // Same-volume move: atomic on NTFS; a failure leaves the staged object for cleanup and
        // no final object observable.
        File.Move(source, destination, overwrite: false);
        return Task.CompletedTask;
    }

    public Task<Stream?> OpenReadAsync(string storageKey, CancellationToken cancellationToken = default)
    {
        ValidateKey(storageKey, FileStorageKey.Prefix);
        var path = ResolvePath(storageKey);
        return Task.FromResult<Stream?>(File.Exists(path)
            ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan)
            : null);
    }

    public Task<bool> ExistsAsync(string storageKey, CancellationToken cancellationToken = default)
    {
        ValidateKey(storageKey, FileStorageKey.Prefix);
        return Task.FromResult(File.Exists(ResolvePath(storageKey)));
    }

    public Task DeleteAsync(string storageKey, CancellationToken cancellationToken = default)
    {
        // Missing keys are tolerated: cleanup after a successful move must be a no-op. Cleanup
        // may target a final object or a staged one that never got promoted; each prefix is
        // validated against its own shape.
        var prefix = storageKey.StartsWith(FileStorageKey.StagingPrefix, StringComparison.Ordinal)
            ? FileStorageKey.StagingPrefix
            : FileStorageKey.Prefix;
        ValidateKey(storageKey, prefix);
        try { File.Delete(ResolvePath(storageKey)); }
        catch (FileNotFoundException) { } catch (DirectoryNotFoundException) { }
        return Task.CompletedTask;
    }

    private string EnsureRoot()
    {
        if (_resolvedRoot is not null) return _resolvedRoot;
        lock (_rootLock)
        {
            if (_resolvedRoot is not null) return _resolvedRoot;
            // Startup configuration validation refuses wwwroot roots; the containment check in
            // ResolvePath is the per-operation guarantee that stays in force regardless.
            var root = Path.GetFullPath(_options.LocalRoot);
            Directory.CreateDirectory(root);
            _resolvedRoot = root;
            return root;
        }
    }

    private string ResolvePath(string key)
    {
        var root = EnsureRoot();
        // Normalize provider-neutral separators, then reject anything that is not a simple
        // relative segment structure beneath the reserved namespace.
        var relative = key.Replace('/', Path.DirectorySeparatorChar);
        var full = Path.GetFullPath(Path.Combine(root, relative));
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(full, root, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Storage key escapes the private storage root.", nameof(key));
        EnsureNoReparsePoints(root, full);
        return full;
    }

    // String-prefix containment cannot see through reparse points: a junction/symlink planted
    // below the root (e.g. root\clinic-files -> C:\outside) would redirect otherwise-valid keys
    // outside the private tree. Normal application operations only create the fixed-named
    // clinic-files/_staging directories and hex-named files, so the application itself can never
    // plant one; every existing component below the root is therefore refused if it is a reparse
    // point. The root directory itself is operator-controlled (see docs/file-storage-phase-1.md).
    private static void EnsureNoReparsePoints(string root, string fullPath)
    {
        var current = root;
        foreach (var segment in Path.GetRelativePath(root, fullPath)
                     .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                         StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            try
            {
                if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                    throw new IOException(
                        $"Refusing storage path through a reparse point below the private storage root: '{current}'.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            // Missing components are normal: not-yet-created staging/final files and directories.
        }
    }

    private static void ValidateKey(string key, string requiredPrefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (!key.StartsWith(requiredPrefix, StringComparison.Ordinal) ||
            key.Length > Domain.Entities.StoredFile.MaxStorageKeyLength ||
            key.AsSpan(requiredPrefix.Length).IndexOfAny(['/', '\\', ':', '\0']) >= 0 ||
            key.Contains("..", StringComparison.Ordinal))
            throw new ArgumentException("Storage key is not a valid opaque provider key.", nameof(key));
    }
}
