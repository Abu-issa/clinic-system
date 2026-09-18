using System.Security.Cryptography;
using Clinic.Application.Attachments;
using Clinic.Application.Storage;
using Clinic.Infrastructure.Storage;
using Microsoft.Extensions.Options;

namespace Clinic.UnitTests;

public sealed class AttachmentStreamGateTests
{
    private sealed class Source(byte[] bytes, bool fail = false) : Stream
    {
        private int position;
        public bool Disposed { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (fail) throw new IOException("synthetic read failure");
            if (position == bytes.Length || buffer.Length == 0) return ValueTask.FromResult(0);
            buffer.Span[0] = bytes[position++];
            return ValueTask.FromResult(1);
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
        public override int Read(byte[] b, int o, int c) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long l) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    }

    [Theory]
    [InlineData("x.pdf", "application/pdf", 1)]
    [InlineData("x.jpg", "image/jpeg", 2)]
    [InlineData("x.png", "image/png", 3)]
    public async Task NonSeekableOneByteSourceAndSmallDestinationPreserveEveryByte(string name, string mime, int size)
    {
        var type = AttachmentFilePolicy.Resolve(name, mime);
        var bytes = AttachmentFilePolicy.SignaturePrefix(type.Signature).Concat(Enumerable.Range(0, 256).Select(x => (byte)x)).ToArray();
        await using var stream = new ValidatingAttachmentStream(new Source(bytes), type, bytes.Length);
        Assert.Equal(0, await stream.ReadAsync(Memory<byte>.Empty));
        using var result = new MemoryStream();
        var buffer = new byte[size];
        int read;
        while ((read = await stream.ReadAsync(buffer)) != 0) result.Write(buffer, 0, read);
        Assert.Equal(bytes, result.ToArray());
    }

    [Theory]
    [InlineData("x.pdf", "application/pdf")]
    [InlineData("x.jpg", "image/jpeg")]
    [InlineData("x.png", "image/png")]
    public async Task FullStoragePathPreservesBytesSizeAndHash(string name, string mime)
    {
        var root = Path.Combine(Path.GetTempPath(), "ClinicUnit_gate_" + Guid.NewGuid().ToString("N"));
        var type = AttachmentFilePolicy.Resolve(name, mime);
        var bytes = AttachmentFilePolicy.SignaturePrefix(type.Signature).Concat(Enumerable.Range(0, 4096).Select(x => (byte)x)).ToArray();
        var storage = new LocalFileStorage(Options.Create(new FileStorageOptions { LocalRoot = root, MaxFileSizeBytes = 10000 }));
        try
        {
            await using var input = new ValidatingAttachmentStream(new Source(bytes), type, bytes.Length);
            var staged = await storage.StageAsync(input);
            Assert.Equal(bytes.Length, staged.SizeBytes);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), staged.Sha256);
            var key = FileStorageKey.NewStorageKey();
            await storage.PromoteAsync(staged.StagedKey, key);
            await using var output = (await storage.OpenReadAsync(key))!;
            using var copy = new MemoryStream();
            await output.CopyToAsync(copy);
            Assert.Equal(bytes, copy.ToArray());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("x.pdf", "application/pdf")]
    [InlineData("x.jpg", "image/jpeg")]
    [InlineData("x.png", "image/png")]
    public async Task EmptyOneByteAndAllTruncatedPrefixesFail(string name, string mime)
    {
        var type = AttachmentFilePolicy.Resolve(name, mime);
        var signature = AttachmentFilePolicy.SignaturePrefix(type.Signature);
        for (var n = 0; n < signature.Length; n++)
        {
            await using var stream = new ValidatingAttachmentStream(new Source(signature[..n]), type, 100);
            var error = await Record.ExceptionAsync(() => stream.CopyToAsync(Stream.Null));
            if (n == 0) Assert.IsType<EmptyFileException>(error);
            else Assert.IsType<UnsupportedAttachmentTypeException>(error);
        }
    }

    [Theory]
    [InlineData("x.pdf", "application/pdf", "jpg")]
    [InlineData("x.jpg", "image/jpeg", "pdf")]
    [InlineData("x.png", "image/png", "pdf")]
    public async Task MismatchedBytesFail(string name, string mime, string actual)
    {
        var bytes = actual == "jpg" ? new byte[] { 255, 216, 255, 0, 1, 2, 3, 4 } : "%PDF-fake png"u8.ToArray();
        await using var stream = new ValidatingAttachmentStream(new Source(bytes), AttachmentFilePolicy.Resolve(name, mime), 100);
        await Assert.ThrowsAsync<UnsupportedAttachmentTypeException>(() => stream.CopyToAsync(Stream.Null));
    }

    [Fact]
    public void MimeParametersAreDeliberatelyRejected()
        => Assert.Throws<UnsupportedAttachmentTypeException>(() => AttachmentFilePolicy.Resolve("x.pdf", "application/pdf; charset=utf-8"));

    [Fact]
    public async Task CancellationAndReadFailurePropagateAndDisposeSource()
    {
        var type = AttachmentFilePolicy.Resolve("x.pdf", "application/pdf");
        var source = new Source("%PDF-hello"u8.ToArray());
        await using (var stream = new ValidatingAttachmentStream(source, type, 100))
        {
            using var cancel = new CancellationTokenSource();
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stream.ReadAsync(new byte[1], cancel.Token).AsTask());
        }
        Assert.True(source.Disposed);
        var broken = new Source([], true);
        await using (var stream = new ValidatingAttachmentStream(broken, type, 100))
            await Assert.ThrowsAsync<IOException>(() => stream.CopyToAsync(Stream.Null));
        Assert.True(broken.Disposed);
    }
}
