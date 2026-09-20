using Clinic.Domain.Entities;
using Clinic.Domain.Enums;

namespace Clinic.UnitTests;

public sealed class NotebookDomainTests
{
    [Fact]
    public void PageCreationRecordsMetadataRevisionAndNormalizesTime()
    {
        var now = new DateTimeOffset(2026, 9, 19, 12, 0, 0, TimeSpan.FromHours(3));
        var page = new NotebookPage(Guid.NewGuid(), null, Guid.NewGuid(), " Note ", "staff", now, null, null);
        Assert.Equal("Note", page.Title);
        Assert.Equal(TimeSpan.Zero, page.CreatedAtUtc.Offset);
        Assert.Equal(now, page.CreatedAtUtc);
        Assert.Equal(page.CreatedAtUtc, page.UpdatedAtUtc);
        Assert.Null(page.FinalizedAtUtc);
        var revision = Assert.Single(page.Revisions);
        Assert.Equal(page.Id, revision.PageId);
        Assert.Equal(page.CurrentRevisionNumber, revision.RevisionNumber);
        Assert.Equal("staff", revision.AuthorStaffId);
        Assert.Equal(NotebookRevisionKind.Created, revision.Kind);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankTitleIsRejected(string title) => Assert.Throws<ArgumentException>(() =>
        new NotebookPage(Guid.NewGuid(), null, Guid.NewGuid(), title, "staff", DateTimeOffset.UtcNow, null, null));

    [Fact]
    public void InvalidRevisionMetadataIsRejected()
    {
        var id = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        Assert.Throws<ArgumentException>(() => new NotebookRevision(id, 0, "staff", now, NotebookRevisionKind.Created, null, null));
        Assert.Throws<ArgumentException>(() => new NotebookRevision(id, 1, "", now, NotebookRevisionKind.Created, null, null));
        Assert.Throws<ArgumentException>(() => new NotebookRevision(id, 1, "staff", now, NotebookRevisionKind.Created, new string('x', 129), null));
        Assert.Throws<ArgumentOutOfRangeException>(() => new NotebookRevision(id, 1, "staff", now, (NotebookRevisionKind)999, null, null));
    }
}
