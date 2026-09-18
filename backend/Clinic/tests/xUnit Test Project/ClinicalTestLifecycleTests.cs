using Clinic.Domain.Entities;
using Clinic.Domain.Enums;

namespace Clinic.UnitTests;

public sealed class ClinicalTestLifecycleTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 18, 0, 0, 0, TimeSpan.Zero);
    private static ClinicalTestRequest Order() => new(Guid.NewGuid(), null, Guid.NewGuid(), ClinicalTestCategory.Lab, "فحص CBC", null, Now);

    [Fact]
    public void MultipleResultsPreserveFirstTimestampAndReviewState()
    {
        var request = Order();
        request.RecordResult(Now.AddMinutes(1));
        Assert.Equal(ClinicalTestStatus.Uploaded, request.Status);
        request.RecordResult(Now.AddMinutes(2));
        Assert.Equal(Now.AddMinutes(1), request.UploadedAtUtc);
        request.StartReview();
        request.RecordResult(Now.AddMinutes(3));
        Assert.Equal(ClinicalTestStatus.UnderReview, request.Status);
        Assert.Equal(Now.AddMinutes(1), request.UploadedAtUtc);
        var doctor = Guid.NewGuid();
        request.CompleteReview(doctor, Now.AddMinutes(4));
        Assert.Equal(ClinicalTestStatus.Reviewed, request.Status);
        Assert.Equal(doctor, request.ReviewedByDoctorId);
        Assert.Equal(Now.AddMinutes(4), request.ReviewedAtUtc);
        Assert.Throws<InvalidOperationException>(() => request.RecordResult(Now.AddMinutes(5)));
        Assert.Throws<InvalidOperationException>(() => request.StartReview());
        Assert.Throws<InvalidOperationException>(() => request.CompleteReview(Guid.NewGuid(), Now.AddMinutes(5)));
        Assert.Equal(doctor, request.ReviewedByDoctorId);
        Assert.Equal(Now.AddMinutes(4), request.ReviewedAtUtc);
        Assert.Empty(request.RowVersion); // Only SQL generates tokens.
        Assert.Equal("فحص CBC", request.TestName);
    }

    [Fact]
    public void InvalidTransitionsTimesAndReviewerAreRejected()
    {
        var request = Order();
        Assert.Throws<InvalidOperationException>(() => request.StartReview());
        Assert.Throws<InvalidOperationException>(() => request.CompleteReview(Guid.NewGuid(), Now));
        Assert.Throws<ArgumentException>(() => request.RecordResult(Now.AddSeconds(-1)));
        request.RecordResult(Now.AddMinutes(1));
        Assert.Throws<InvalidOperationException>(() => request.CompleteReview(Guid.NewGuid(), Now));
        request.StartReview();
        Assert.Throws<InvalidOperationException>(() => request.StartReview());
        Assert.Throws<ArgumentException>(() => request.CompleteReview(Guid.Empty, Now.AddMinutes(2)));
        Assert.Throws<ArgumentException>(() => request.CompleteReview(Guid.NewGuid(), Now));
        Assert.Equal(ClinicalTestStatus.UnderReview, request.Status);
        Assert.Null(request.ReviewedAtUtc);
    }

    [Fact]
    public void LinkRequiresSamePatientAndDoesNotDuplicateMetadata()
    {
        var request = Order();
        var attachment = new PatientAttachment(request.PatientId, Guid.NewGuid(), null, "actor", Now);
        var link = new ClinicalTestResultAttachment(request, attachment, Now);
        Assert.Equal(request.Id, link.ClinicalTestRequestId);
        Assert.Equal(attachment.Id, link.PatientAttachmentId);
        Assert.Equal(Now, link.LinkedAtUtc);
        Assert.Throws<ArgumentException>(() => new ClinicalTestResultAttachment(request,
            new PatientAttachment(Guid.NewGuid(), Guid.NewGuid(), null, "actor", Now), Now));
        Assert.All(typeof(ClinicalTestResultAttachment).GetProperties(), p => Assert.False(p.SetMethod?.IsPublic == true));
        Assert.Equal(4, typeof(ClinicalTestResultAttachment).GetProperties().Length);
    }

    [Fact]
    public void AttachmentDtoHasOnlySafeFields() => Assert.Equal(
        new[] { "AttachmentId", "ContentType", "CreatedAtUtc", "OriginalFileName", "SizeBytes" },
        typeof(Clinic.Application.ClinicalTests.ClinicalTestAttachmentDetails).GetProperties().Select(p => p.Name).OrderBy(x => x));
}
