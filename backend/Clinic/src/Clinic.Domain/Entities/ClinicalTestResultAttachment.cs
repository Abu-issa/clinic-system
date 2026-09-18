namespace Clinic.Domain.Entities;

public sealed class ClinicalTestResultAttachment
{
    private ClinicalTestResultAttachment() { }
    public ClinicalTestResultAttachment(ClinicalTestRequest request, PatientAttachment attachment, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(attachment);
        if (request.PatientId != attachment.PatientId) throw new ArgumentException("Result must belong to the request patient.");
        Id = Guid.NewGuid();
        ClinicalTestRequestId = request.Id;
        PatientAttachmentId = attachment.Id;
        LinkedAtUtc = now.ToUniversalTime();
    }
    public Guid Id { get; private set; }
    public Guid ClinicalTestRequestId { get; private set; }
    public Guid PatientAttachmentId { get; private set; }
    public DateTimeOffset LinkedAtUtc { get; private set; }
}
