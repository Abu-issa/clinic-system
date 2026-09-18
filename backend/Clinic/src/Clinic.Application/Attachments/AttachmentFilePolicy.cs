using System.Text;
using Clinic.Application.Storage;

namespace Clinic.Application.Attachments;

/// <summary>
/// Conservative feature-level file policy for clinical attachments (the generic storage
/// foundation deliberately has none). Only PDF/JPEG/PNG with matching extension and declared
/// content type are accepted, and the declared type must also match the file's leading magic
/// bytes. Declared metadata is still NOT proof of safety: no antivirus or deep content
/// inspection exists, downloads are always served as attachments with nosniff, and the
/// allowlist must stay narrow. Extension/MIME policy is validated eagerly; the signature is
/// verified while streaming the first bytes (never whole-file buffering).
/// </summary>
public static class AttachmentFilePolicy
{
    public enum SignatureKind { Pdf, Jpeg, Png }

    public sealed record AllowedType(string Extension, string ContentType, SignatureKind Signature, string HeaderName);

    private static readonly AllowedType[] Allowed =
    [
        new(".pdf", "application/pdf", SignatureKind.Pdf, "pdf"),
        new(".jpg", "image/jpeg", SignatureKind.Jpeg, "jpg"),
        new(".jpeg", "image/jpeg", SignatureKind.Jpeg, "jpg"),
        new(".png", "image/png", SignatureKind.Png, "png"),
    ];

    /// <summary>Magic bytes required for each accepted type (checked against the upload prefix).</summary>
    public static byte[] SignaturePrefix(SignatureKind kind) => kind switch
    {
        SignatureKind.Pdf => "%PDF-"u8.ToArray(),                       // 5 bytes
        SignatureKind.Jpeg => [0xFF, 0xD8, 0xFF],                       // 3 bytes
        SignatureKind.Png => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A], // 8 bytes
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>Longest signature across the allowlist — enough prefix bytes to validate any kind.</summary>
    public const int MaxSignatureLength = 8;

    /// <summary>
    /// Resolves the accepted type for an extension + declared content-type pair. The extension
    /// is compared on the *display* filename's final segment only (path components must already
    /// be stripped); hostile or unpaired declarations are rejected before any bytes stream.
    /// </summary>
    public static AllowedType Resolve(string originalFileName, string contentType)
    {
        var name = originalFileName?.Trim() ?? string.Empty;
        var extension = Path.GetExtension(name).ToLowerInvariant();
        var type = contentType?.Trim().ToLowerInvariant() ?? string.Empty;
        var match = Allowed.FirstOrDefault(a => a.Extension == extension && a.ContentType == type);
        if (match is null)
            throw new UnsupportedAttachmentTypeException(
                "Attachments accept only PDF, JPEG, or PNG with a matching declared content type.");
        return match;
    }

    /// <summary>Validates the leading magic bytes for the resolved type; null-safe for short files.</summary>
    public static void ValidateSignature(AllowedType type, ReadOnlySpan<byte> prefix)
    {
        var expected = SignaturePrefix(type.Signature);
        if (prefix.Length < expected.Length || !prefix[..expected.Length].SequenceEqual(expected))
            throw new UnsupportedAttachmentTypeException(
                "The file content does not match its declared attachment type.");
    }
}

/// <summary>The upload failed the extension/MIME/signature allowlist. Never a safety claim:
/// this policy is a narrow format gate, not malware protection.</summary>
public sealed class UnsupportedAttachmentTypeException(string message) : Exception(message);

/// <summary>
/// Wraps the upload stream for the storage stage: verifies the magic-byte signature as the
/// first bytes flow and enforces the feature-level size limit during streaming (the storage
/// foundation's absolute maximum remains the independent backstop). Never buffers the whole
/// upload; at most one small signature prefix is held.
/// </summary>
public sealed class ValidatingAttachmentStream(Stream inner, AttachmentFilePolicy.AllowedType type,
    long maxBytes, bool leaveOpen = false) : Stream
{
    private readonly byte[] _signatureBuffer = new byte[AttachmentFilePolicy.MaxSignatureLength];
    private int _signatureFilled;
    private bool _signatureChecked;
    private long _counted;

    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => inner.Flush();
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override async ValueTask DisposeAsync()
    {
        if (!leaveOpen) await inner.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !leaveOpen) inner.Dispose();
        base.Dispose(disposing);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (buffer.Length == 0) return 0;
        if (!_signatureChecked)
        {
            while (_signatureFilled < _signatureBuffer.Length)
            {
                var read = await inner.ReadAsync(_signatureBuffer.AsMemory(_signatureFilled), cancellationToken);
                if (read == 0) break;
                _signatureFilled += read;
            }
            if (_signatureFilled == 0) throw new EmptyFileException();
            AttachmentFilePolicy.ValidateSignature(type, _signatureBuffer.AsSpan(0, _signatureFilled));
            _signatureChecked = true;
        }

        var copied = Math.Min(buffer.Length, _signatureFilled);
        _signatureBuffer.AsMemory(0, copied).CopyTo(buffer);
        _signatureFilled -= copied;
        _signatureBuffer.AsSpan(copied, _signatureFilled).CopyTo(_signatureBuffer);
        var readFromInner = copied < buffer.Length
            ? await inner.ReadAsync(buffer[copied..], cancellationToken) : 0;
        _counted += copied + readFromInner;
        EnsureWithinLimit();
        return copied + readFromInner;
    }
    private void EnsureWithinLimit()
    {
        if (_counted > maxBytes)
            throw new FileSizeLimitExceededException(maxBytes);
    }
}
