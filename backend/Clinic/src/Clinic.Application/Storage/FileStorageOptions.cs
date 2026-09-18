namespace Clinic.Application.Storage;

/// <summary>
/// Strongly typed file-storage configuration, validated at startup. MaxFileSizeBytes is the
/// absolute safety ceiling for every store operation; future feature modules enforce their own
/// stricter limits on top. Provider is limited to "Local" until a production object-storage
/// provider is selected and implemented; no S3 credentials are read or configured anywhere.
/// </summary>
public sealed class FileStorageOptions
{
    public const string SectionName = "FileStorage";
    public const long AbsoluteMaxFileSizeBytes = 1_073_741_824; // 1 GiB hard ceiling.
    public const long DefaultMaxFileSizeBytes = 26_214_400;     // 25 MiB development default.

    public string Provider { get; init; } = "Local";
    public string LocalRoot { get; init; } = "./data/files";
    public long MaxFileSizeBytes { get; init; } = DefaultMaxFileSizeBytes;

    /// <summary>Reviewed operator opt-in required before production may serve files from local
    /// disk. Default false: production refuses Local storage unless this is explicitly true.</summary>
    public bool AllowLocalInProduction { get; init; }

    public bool IsValid() =>
        Provider == "Local" &&
        !string.IsNullOrWhiteSpace(LocalRoot) &&
        LocalRoot.Length <= 400 &&
        LocalRoot.IndexOfAny(PathInvalidRootCharacters) < 0 &&
        MaxFileSizeBytes is >= 1 and <= AbsoluteMaxFileSizeBytes;

    public bool IsOutsideWebRoot(string webRoot)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(LocalRoot));
        var publicRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(webRoot));
        return !string.Equals(root, publicRoot, StringComparison.OrdinalIgnoreCase) &&
            !root.StartsWith(publicRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    // A root containing these characters is either a Windows drive/UNC/device path escape or
    // unusable; deployments configure a clean private directory path instead.
    private static readonly char[] PathInvalidRootCharacters = ['"', '<', '>', '|', '*', '?', '\0'];
}
