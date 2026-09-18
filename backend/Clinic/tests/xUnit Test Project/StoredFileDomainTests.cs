using Clinic.Domain.Entities;

namespace Clinic.UnitTests;

public sealed class StoredFileDomainTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 17, 10, 0, 0, TimeSpan.Zero);

    private static StoredFile File(
        string? storageKey = null,
        string? name = null,
        string? contentType = null,
        long size = 10,
        string? sha256 = null,
        string? actor = "staff-1",
        DateTimeOffset? created = null) =>
        new(storageKey ?? $"clinic-files/{new string('a', 32)}",
            name ?? "report.pdf", contentType ?? "application/pdf", size,
            sha256 ?? new string('0', 64), actor, created ?? Now);

    private static string ValidKey() => $"clinic-files/{Guid.NewGuid():n}";

    [Fact]
    public void ValidConstructionPreservesServerFacts()
    {
        var key = ValidKey();
        var file = File(storageKey: key, name: "  scan-1.pdf  ", actor: " staff-9 ");
        Assert.NotEqual(Guid.Empty, file.Id);
        Assert.Equal(key, file.StorageKey);
        Assert.Equal("scan-1.pdf", file.OriginalFileName);
        Assert.Equal("staff-9", file.CreatedByStaffId);
        Assert.Equal(Now, file.CreatedAtUtc);
    }

    [Fact]
    public void TimestampIsNormalizedToUtc()
    {
        var local = new DateTimeOffset(2026, 9, 17, 13, 0, 0, TimeSpan.FromHours(3));
        Assert.Equal(Now, File(created: local).CreatedAtUtc);
    }

    [Fact]
    public void KeyMustBeOpaqueServerGeneratedShape()
    {
        // Only 'clinic-files/{32 hex}' keys are valid: client-derived names can never persist.
        Assert.Throws<ArgumentException>(() => File(storageKey: "uploads/report.pdf"));
        Assert.Throws<ArgumentException>(() => File(storageKey: "clinic-files/../secret.pdf"));
        Assert.Throws<ArgumentException>(() => File(storageKey: "C:\\private\\file.bin"));
        Assert.Throws<ArgumentException>(() => File(storageKey: "clinic-files/ABC!"));
        Assert.Throws<ArgumentException>(() => File(storageKey: ""));
        Assert.Throws<ArgumentException>(() => File(storageKey: new string('a', 300)));
    }

    [Theory]
    [InlineData("../../../etc/passwd")]
    [InlineData("..\\..\\win.ini")]
    [InlineData("C:\\Users\\victim\\secret.txt")]
    [InlineData("\\\\server\\share\\file.txt")]
    [InlineData("report.pdf\u0000.exe")]
    public void HostileOriginalFileNamesAreBoundedDisplayMetadataOnly(string name)
    {
        // Control characters are rejected; separators are tolerated because the name never
        // participates in key or path construction (the key stays server-generated).
        if (name.Contains('\0'))
            Assert.Throws<ArgumentException>(() => File(name: name));
        else
            Assert.Equal(name.Trim(), File(name: name).OriginalFileName);
    }

    [Fact]
    public void OriginalFileNameBounds()
    {
        Assert.Throws<ArgumentException>(() => File(name: "  \t "));
        Assert.Throws<ArgumentException>(() => File(name: new string('x', 256)));
        Assert.Equal(255, File(name: new string('x', 255)).OriginalFileName.Length);
    }

    [Fact]
    public void ContentTypeIsRequiredAndBounded()
    {
        Assert.Throws<ArgumentException>(() => File(contentType: ""));
        Assert.Throws<ArgumentException>(() => File(contentType: new string('a', 256)));
        Assert.Throws<ArgumentException>(() => File(contentType: "text/plain\r\nX-Injected: 1"));
        Assert.Equal("application/pdf", File().ContentType);
    }

    [Fact]
    public void ZeroByteAndInvalidHashAreRejected()
    {
        Assert.Throws<ArgumentException>(() => File(size: 0));
        Assert.Throws<ArgumentException>(() => File(size: -1));
        Assert.Throws<ArgumentException>(() => File(sha256: new string('G', 64)));
        Assert.Throws<ArgumentException>(() => File(sha256: new string('a', 63)));
    }

    [Fact]
    public void ActorIsOptionalAndBounded()
    {
        Assert.Null(File(actor: "  ").CreatedByStaffId);
        Assert.Throws<ArgumentException>(() => File(actor: new string('x', 451)));
        Assert.Equal(450, File(actor: new string('x', 450)).CreatedByStaffId!.Length);
    }
}
