using Clinic.Domain.Entities;
using Clinic.Domain.Enums;

namespace Clinic.UnitTests;

public sealed class NotebookLifecycleTests
{
    [Fact]
    public void FinalizationIsPermanentAndOnlyAmendmentsCanFollow()
    {
        var now = DateTimeOffset.UtcNow;
        var doctor = Guid.NewGuid();
        var page = new NotebookPage(Guid.NewGuid(), null, doctor, "Note", "staff", now, null, null);
        Assert.Throws<InvalidOperationException>(() => page.AppendAmendment(Guid.NewGuid(), "staff", now, "a", "d"));
        var payload = page.AppendPayload(Guid.NewGuid(), "staff", now, "p", "d");
        Assert.True(page.FinalizePage(doctor, now.AddSeconds(1)));
        var finalized = page.FinalizedAtUtc;
        Assert.False(page.FinalizePage(Guid.NewGuid(), now.AddSeconds(2)));
        Assert.Equal(finalized, page.FinalizedAtUtc);
        Assert.Equal(doctor, page.FinalizedByDoctorId);
        Assert.Throws<InvalidOperationException>(() => page.AppendPayload(Guid.NewGuid(), "staff", now, "x", "d"));
        var amendment = page.AppendAmendment(Guid.NewGuid(), "other", now.AddSeconds(3), "a", "d");
        Assert.Equal(NotebookRevisionKind.Amendment, amendment.Kind);
        Assert.Equal(payload.RevisionNumber + 1, amendment.RevisionNumber);
        Assert.Equal(finalized, page.FinalizedAtUtc);
        Assert.Equal(doctor, page.FinalizedByDoctorId);
        Assert.Equal(now.AddSeconds(3), page.UpdatedAtUtc);
        Assert.Equal(NotebookRevisionKind.Payload, payload.Kind);
    }

    [Fact]
    public void AmendmentRequiresStoredPayloadAndIdentifiers()
    {
        Assert.Throws<ArgumentException>(() => new NotebookRevision(Guid.NewGuid(), 2, "staff", DateTimeOffset.UtcNow,
            NotebookRevisionKind.Amendment, "a", "d"));
        Assert.Throws<ArgumentException>(() => new NotebookRevision(Guid.NewGuid(), 2, "staff", DateTimeOffset.UtcNow,
            NotebookRevisionKind.Amendment, null, "d", Guid.NewGuid()));
    }
}
