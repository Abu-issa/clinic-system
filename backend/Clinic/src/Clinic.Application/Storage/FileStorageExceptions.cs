namespace Clinic.Application.Storage;

/// <summary>The payload exceeded the configured absolute size limit; measured while streaming,
/// so an undeclared oversized body is always caught.</summary>
public sealed class FileSizeLimitExceededException(long maxBytes) : Exception($"File exceeds the configured maximum size of {maxBytes} bytes.")
{
    public long MaxBytes { get; } = maxBytes;
}

/// <summary>Zero-byte payloads carry no clinical value and usually indicate a failed read;
/// they are rejected before any persistence.</summary>
public sealed class EmptyFileException() : Exception("Empty files are not accepted.");
