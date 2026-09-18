using System.Text;
using Clinic.Application.Attachments;
using Clinic.Application.Audit;
using Clinic.Application.Storage;
using Clinic.Domain.Entities;
using Clinic.Infrastructure.Storage;
using Microsoft.Extensions.Options;

namespace Clinic.UnitTests;

public sealed class AttachmentTests : IDisposable
{
    // ---------- policy ----------

    [Theory]
    [InlineData("report.pdf", "application/pdf")]
    [InlineData("report.PDF", "Application/PDF")]
    [InlineData("scan.jpg", "image/jpeg")]
    [InlineData("scan.jpeg", "image/jpeg")]
    [InlineData("image.png", "image/png")]
    public void ValidTypePairsResolve(string name, string contentType)
    {
        var resolved = AttachmentFilePolicy.Resolve(name, contentType);
        Assert.Equal(contentType.ToLowerInvariant(), resolved.ContentType);
    }

    [Theory]
    [InlineData("payload.exe", "application/pdf")]
    [InlineData("page.html", "text/html")]
    [InlineData("vector.svg", "image/svg+xml")]
    [InlineData("archive.zip", "application/zip")]
    [InlineData("script.js", "text/javascript")]
    [InlineData("noextension", "application/pdf")]
    [InlineData("report.pdf", "image/png")] // wrong pairing
    [InlineData("image.png", "application/pdf")]
    public void UnsupportedOrMismatchedTypesAreRejected(string name, string contentType)
    {
        Assert.Throws<UnsupportedAttachmentTypeException>(() => AttachmentFilePolicy.Resolve(name, contentType));
    }

    private sealed class ChunkedStream(byte[] bytes, int chunk) : MemoryStream(bytes)
    {
        private readonly int _chunk = chunk;
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
            => await base.ReadAsync(buffer.AsMemory(offset, Math.Min(count, _chunk)), ct);
    }

    [Fact]
    public async Task ValidSignaturesPassWhileStreaming()
    {
        await AssertSigns("report.pdf", "application/pdf", "%PDF-1.7 ..."u8.ToArray());
        await AssertSigns("scan.jpg", "image/jpeg", [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3]);
        await AssertSigns("image.png", "image/png", [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 9, 9]);
    }

    private static async Task AssertSigns(string name, string contentType, byte[] bytes)
    {
        var type = AttachmentFilePolicy.Resolve(name, contentType);
        await using var stream = new ValidatingAttachmentStream(new MemoryStream(bytes), type, 1_000_000);
        var buffer = new byte[bytes.Length + 10];
        Assert.Equal(bytes.Length, await stream.ReadAsync(buffer));
        Assert.Equal(bytes, buffer[..bytes.Length]);
    }

    [Fact]
    public async Task FakePdfIsRejectedBySignatureEvenWithCorrectNameAndMime()
    {
        var type = AttachmentFilePolicy.Resolve("fake.pdf", "application/pdf");
        await using var stream = new ValidatingAttachmentStream(
            new ChunkedStream("<html>not a pdf</html>"u8.ToArray(), 1), type, 1_000_000);
        await Assert.ThrowsAsync<UnsupportedAttachmentTypeException>(
            () => stream.ReadAsync(new byte[64]).AsTask());
    }

    [Fact]
    public async Task SignatureValidationWorksAcrossOneByteChunks()
    {
        var type = AttachmentFilePolicy.Resolve("image.png", "image/png");
        await using var good = new ValidatingAttachmentStream(
            new ChunkedStream([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 7], 1), type, 1_000);
        Assert.Equal(9, await good.ReadAsync(new byte[64]));

        await using var bad = new ValidatingAttachmentStream(
            new ChunkedStream([0x89, 0x00, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A], 1), type, 1_000);
        await Assert.ThrowsAsync<UnsupportedAttachmentTypeException>(
            () => bad.ReadAsync(new byte[64]).AsTask());
    }

    [Fact]
    public async Task FeatureSizeLimitIsEnforcedDuringStreaming()
    {
        var type = AttachmentFilePolicy.Resolve("report.pdf", "application/pdf");
        await using var stream = new ValidatingAttachmentStream(
            new MemoryStream("%PDF-long payload exceeds the small feature limit here"u8.ToArray()), type, 16);
        var buffer = new byte[128];
        // The staging loop reads until EOF; the limit trips on a later iteration.
        await Assert.ThrowsAsync<FileSizeLimitExceededException>(async () =>
        {
            int read;
            while ((read = await stream.ReadAsync(buffer).AsTask()) > 0) { }
        });
    }

    // ---------- service ----------

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ClinicUnit_files_{Guid.NewGuid():N}");
    private readonly FixedClock _clock = new() { Now = new DateTimeOffset(2026, 9, 17, 12, 0, 0, TimeSpan.Zero) };

    private sealed class FixedClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; }
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class RecordingAuditWriter : IAuditMutationWriter
    {
        public List<AuditAppendRequest> Appended { get; } = [];
        public int Disposals { get; private set; }
        public bool FailAppend { get; set; }

        public IDisposable BeginMutation() => new Scope(this);

        // Mirrors the real scope's success behavior: disposal detaches only still-Added events,
        // which after a successful save is nothing — so staged events remain observable here.
        private sealed class Scope(RecordingAuditWriter owner) : IDisposable
        {
            public void Dispose() => owner.Disposals++;
        }

        void IAuditMutationWriter.Append(AuditAppendRequest request)
        {
            if (FailAppend) throw new InvalidOperationException("synthetic append failure");
            Appended.Add(request);
        }
    }

    /// <summary>In-memory IAttachmentStore standing in for the SQL store.</summary>
    private sealed class FakeStore : IAttachmentStore
    {
        public List<StoredFile> Files { get; } = [];
        public List<PatientAttachment> Attachments { get; } = [];
        public int Discards { get; private set; }
        public bool FailSave { get; set; }
        private readonly HashSet<Guid> _patients = [];
        private readonly Dictionary<Guid, Guid> _visits = [];
        private readonly Dictionary<Guid, Guid> _visitDoctors = [];

        public void AddPatient(Guid id) => _patients.Add(id);
        public void AddVisit(Guid patientId, Guid visitId, Guid doctorId)
        {
            _visits[visitId] = patientId;
            _visitDoctors[visitId] = doctorId;
        }

        public Task<bool> PatientExistsAsync(Guid patientId, CancellationToken cancellationToken = default) =>
            Task.FromResult(_patients.Contains(patientId));

        public Task<AttachmentVisitReference?> VisitAsync(Guid patientId, Guid visitId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_visits.TryGetValue(visitId, out var owner) && owner == patientId
                ? new AttachmentVisitReference(visitId, _visitDoctors.GetValueOrDefault(visitId))
                : null);

        public void Add(StoredFile file, PatientAttachment attachment)
        {
            Files.Add(file);
            Attachments.Add(attachment);
        }

        public Task<AttachmentDownloadReference?> DownloadAsync(Guid patientId, Guid attachmentId,
            CancellationToken cancellationToken = default) => Task.FromResult<AttachmentDownloadReference?>(null);

        public Task<IReadOnlyList<AttachmentListItem>> ListAsync(Guid patientId, Guid? visitId, int page, int pageSize,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AttachmentListItem>>([]);

        public async Task SaveAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                if (FailSave) throw new InvalidOperationException("synthetic save outage");
                await Task.Yield();
            }
            catch
            {
                DiscardChanges(); // mirror the real store: a rejected save discards its own rows
                throw;
            }
        }

        public void DiscardChanges()
        {
            Discards++;
            Files.Clear();
            Attachments.Clear();
        }
    }

    private LocalFileStorage Provider() => new(Options.Create(
        new FileStorageOptions { Provider = "Local", LocalRoot = _root, MaxFileSizeBytes = 1_000_000 }));

    private static Stream Bytes(string text) => new MemoryStream(Encoding.UTF8.GetBytes(text));

    private static UploadAttachmentRequest Request(Guid patientId, Guid? visitId, Stream content,
        string name = "report.pdf", string contentType = "application/pdf") =>
        new(patientId, visitId, content, name, contentType, "staff-1");

    private (AttachmentService Service, FakeStore Store, RecordingAuditWriter Audit) Build()
    {
        var store = new FakeStore();
        var audit = new RecordingAuditWriter();
        var service = new AttachmentService(Provider(), store, audit,
            new AttachmentOptions { MaxFileSizeBytes = 100_000 }, _clock);
        return (service, store, audit);
    }

    [Fact]
    public async Task SuccessfulUploadPersistsLinkMetadataAndSingleAuditEvent()
    {
        var (service, store, audit) = Build();
        var patient = Guid.NewGuid();
        store.AddPatient(patient);
        var bytes = "%PDF-synthetic attachment"u8.ToArray();

        var result = await service.UploadAsync(Request(patient, null, new MemoryStream(bytes)));

        Assert.True(result.IsSuccess);
        var created = result.Attachment!;
        Assert.Equal(bytes.LongLength, created.SizeBytes);
        Assert.Equal("application/pdf", created.ContentType);
        Assert.Equal(_clock.Now, created.CreatedAtUtc);
        Assert.Null(created.VisitId);

        var file = Assert.Single(store.Files);
        Assert.Equal(created.AttachmentId, Assert.Single(store.Attachments).Id);
        Assert.Equal(file.Id, Assert.Single(store.Attachments).StoredFileId);
        Assert.Equal(patient, Assert.Single(store.Attachments).PatientId);
        Assert.Equal(created.SizeBytes, file.SizeBytes);
        Assert.StartsWith(FileStorageKey.Prefix, file.StorageKey);
        Assert.DoesNotContain(Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories),
            f => f.Contains("_staging", StringComparison.Ordinal));

        var events = Assert.Single(audit.Appended);
        Assert.Equal("file.upload", events.ActionCode);
        Assert.Equal("attachment", events.ResourceType);
        Assert.Equal(created.AttachmentId.ToString("N"), events.ResourceId);
        Assert.Equal(patient, events.PatientId);
        Assert.Equal("staff-1", events.ActorStaffId);
        Assert.Null(events.TraceId);
        Assert.Null(events.Metadata);

        // Exact bytes preserved.
        var provider = Provider();
        await using var stored = (await provider.OpenReadAsync(file.StorageKey))!;
        using var reader = new BinaryReader(stored);
        Assert.Equal(bytes, reader.ReadBytes(bytes.Length));
    }

    [Fact]
    public async Task DuplicateIdenticalUploadsCreateIndependentAttachments()
    {
        var (service, store, audit) = Build();
        var patient = Guid.NewGuid();
        store.AddPatient(patient);
        var bytes = "%PDF-same-bytes"u8.ToArray();

        var first = await service.UploadAsync(Request(patient, null, new MemoryStream(bytes), "same.pdf"));
        var second = await service.UploadAsync(Request(patient, null, new MemoryStream(bytes), "same.pdf"));

        Assert.True(first.IsSuccess && second.IsSuccess);
        Assert.NotEqual(first.Attachment!.AttachmentId, second.Attachment!.AttachmentId);
        Assert.NotEqual(store.Files[0].StorageKey, store.Files[1].StorageKey);
        Assert.Equal(2, store.Files.Count);
        Assert.Equal(2, store.Attachments.Count);
        Assert.Equal(2, audit.Appended.Count);
        // No deduplication by hash: two independent StoredFile rows even with identical bytes.
        Assert.Equal(store.Files[0].Sha256, store.Files[1].Sha256);
    }

    [Fact]
    public async Task ClientPathComponentsAreStrippedFromDisplayMetadata()
    {
        var (service, store, _) = Build();
        var patient = Guid.NewGuid();
        store.AddPatient(patient);

        foreach (var hostile in new[] { "../../evil.pdf", "C:\\fake\\evil.pdf", "\\\\server\\share\\evil.pdf" })
        {
            var result = await service.UploadAsync(Request(patient, null, Bytes("%PDF-x"), hostile));
            Assert.True(result.IsSuccess);
            Assert.Equal("evil.pdf", result.Attachment!.OriginalFileName);
        }
        Assert.All(store.Files, f => Assert.Equal("evil.pdf", f.OriginalFileName));
    }

    [Fact]
    public async Task MissingPatientIsRejectedBeforeAnyByteStreams()
    {
        var (service, store, audit) = Build();
        var unreadable = new UnreadableStream();

        var result = await service.UploadAsync(Request(Guid.NewGuid(), null, unreadable));

        Assert.Equal(AttachmentUploadError.PatientNotFound, result.Error);
        Assert.Empty(store.Files);
        Assert.Empty(store.Attachments);
        Assert.Empty(audit.Appended);
        Assert.False(unreadable.WasRead);
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public async Task CrossPatientVisitIsRejectedBeforeAnyByteStreams()
    {
        var (service, store, audit) = Build();
        var patient = Guid.NewGuid();
        var otherVisit = Guid.NewGuid();
        store.AddPatient(patient);
        store.AddVisit(Guid.NewGuid(), otherVisit, Guid.NewGuid()); // belongs to another patient
        var unreadable = new UnreadableStream();

        var result = await service.UploadAsync(Request(patient, otherVisit, unreadable));

        Assert.Equal(AttachmentUploadError.VisitMismatch, result.Error);
        Assert.Empty(store.Files);
        Assert.Empty(audit.Appended);
        Assert.False(unreadable.WasRead);
    }

    [Fact]
    public async Task UnsupportedTypeLeavesNoStorageArtifacts()
    {
        var (service, store, audit) = Build();
        var patient = Guid.NewGuid();
        store.AddPatient(patient);

        await Assert.ThrowsAsync<UnsupportedAttachmentTypeException>(() =>
            service.UploadAsync(Request(patient, null, Bytes("<html>x</html>"), "page.html", "text/html")));

        Assert.Empty(store.Files);
        Assert.Empty(audit.Appended);
        Assert.False(Directory.Exists(_root)); // stage never began
    }

    [Fact]
    public async Task SaveFailureCompensatesTheObjectAndDropsAllRowsAndAudit()
    {
        var (service, store, audit) = Build();
        store.FailSave = true;
        var patient = Guid.NewGuid();
        store.AddPatient(patient);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.UploadAsync(Request(patient, null, Bytes("%PDF-x"))));

        // Store discarded the staged rows; the promoted object was compensated. The audit event
        // was only ever staged inside the failed save — in the real store the mutation scope
        // detaches it and the SQL integration tests prove nothing committed.
        Assert.Empty(store.Files);
        Assert.Empty(store.Attachments);
        Assert.True(store.Discards >= 1);
        Assert.Single(audit.Appended);
        Assert.False(Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories).Any());
    }

    [Fact]
    public async Task AuditAppendFailureAlsoCompensatesPromotedObject()
    {
        var (service, store, audit) = Build();
        var patient = Guid.NewGuid();
        store.AddPatient(patient);
        audit.FailAppend = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.UploadAsync(Request(patient, null, Bytes("%PDF-x"))));
        Assert.Empty(store.Files);
        Assert.Empty(store.Attachments);
        Assert.Empty(audit.Appended);
        Assert.Empty(Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories));
    }
    private sealed class UnreadableStream : MemoryStream
    {
        public bool WasRead { get; private set; }
        public UnreadableStream() : base("%PDF-nothing"u8.ToArray()) { }
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            WasRead = true;
            return await base.ReadAsync(buffer.AsMemory(offset, count), ct);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
