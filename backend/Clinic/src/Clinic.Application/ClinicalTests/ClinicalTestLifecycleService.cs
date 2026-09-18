using Clinic.Application.Attachments;
using Clinic.Application.Audit;
using Clinic.Application.Exceptions;
using Clinic.Domain.Entities;
using Clinic.Domain.Enums;

namespace Clinic.Application.ClinicalTests;

public sealed class ClinicalTestLifecycleService(IClinicalTestStore store, AttachmentService attachments,
    IAuditMutationWriter audit, TimeProvider clock)
{
    public async Task<bool> HasDoctorAuthorityAsync(Guid patient, Guid id, string actor, CancellationToken ct)
    {
        var request = await store.LoadAsync(patient, id, ct);
        return request is not null && await DoctorAsync(request, actor, ct) is not null;
    }
    public async Task<ClinicalTestError> CheckUploadAsync(Guid patient, Guid id, string actor, bool isDoctor, CancellationToken ct)
    {
        var request = await store.LoadAsync(patient, id, ct);
        if (request is null) return ClinicalTestError.RequestNotFound;
        if (isDoctor && await DoctorAsync(request, actor, ct) is null) return ClinicalTestError.DoctorAuthority;
        return request.Status == ClinicalTestStatus.Reviewed ? ClinicalTestError.InvalidTransition : ClinicalTestError.None;
    }

    private async Task<Guid?> DoctorAsync(ClinicalTestRequest request, string actor, CancellationToken ct)
    {
        var doctor = await store.AssociatedDoctorAsync(actor, ct);
        if (doctor is null || doctor == Guid.Empty) return null;
        if (request.VisitId is { } visit && await store.VisitDoctorAsync(request.PatientId, visit, ct) != doctor) return null;
        return doctor;
    }

    public async Task<ClinicalTestResult> UploadAsync(Guid patient, Guid id, byte[] expected, string actor,
        bool isDoctor, Stream content, string filename, string contentType, CancellationToken ct = default)
    {
        await using var owned = content;
        if (expected is not { Length: 8 }) return new(ClinicalTestError.InvalidInput);
        var error = await CheckUploadAsync(patient, id, actor, isDoctor, ct);
        if (error != ClinicalTestError.None) return new(error);
        var request = (await store.LoadAsync(patient, id, ct))!;
        if (!request.RowVersion.SequenceEqual(expected)) return new(ClinicalTestError.Conflict);
        var count = (await store.GetAsync(patient, id, ct))!.ResultAttachmentCount;
        try
        {
            var upload = await attachments.UploadClinicalResultAsync(new(patient, request.VisitId, content, filename, contentType, actor), attachment =>
            {
                request.RecordResult(clock.GetUtcNow());
                store.ExpectVersion(request, expected);
                store.AddResult(new(request, attachment, clock.GetUtcNow()));
                Append(actor, request, "test-request.result.upload");
            }, ct);
            if (!upload.IsSuccess) return new(ClinicalTestError.RequestNotFound);
        }
        catch (PersistenceConcurrencyException) { store.DiscardChanges(); return new(ClinicalTestError.Conflict); }
        catch { store.DiscardChanges(); throw; }
        return new(ClinicalTestError.None, Map(request, count + 1));
    }

    public async Task<ClinicalTestResult> ReviewAsync(Guid patient, Guid id, byte[] expected, string actor,
        bool complete, CancellationToken ct = default)
    {
        if (expected is not { Length: 8 }) return new(ClinicalTestError.InvalidInput);
        var request = await store.LoadAsync(patient, id, ct);
        if (request is null) return new(ClinicalTestError.RequestNotFound);
        var doctor = await DoctorAsync(request, actor, ct);
        if (doctor is null) return new(ClinicalTestError.DoctorAuthority);
        if (!request.RowVersion.SequenceEqual(expected)) return new(ClinicalTestError.Conflict);
        if (request.Status != (complete ? ClinicalTestStatus.UnderReview : ClinicalTestStatus.Uploaded))
            return new(ClinicalTestError.InvalidTransition);
        var count = (await store.GetAsync(patient, id, ct))!.ResultAttachmentCount;
        using var scope = audit.BeginMutation();
        try
        {
            if (complete) request.CompleteReview(doctor.Value, clock.GetUtcNow());
            else request.StartReview();
            store.ExpectVersion(request, expected);
            Append(actor, request, complete ? "test-request.review.complete" : "test-request.review.start");
            await store.SaveAsync(ct);
        }
        catch (PersistenceConcurrencyException) { store.DiscardChanges(); return new(ClinicalTestError.Conflict); }
        catch { store.DiscardChanges(); throw; }
        return new(ClinicalTestError.None, Map(request, count));
    }

    private static ClinicalTestDetails Map(ClinicalTestRequest x, int count) => new(x.Id, x.PatientId, x.VisitId,
        x.Category, x.TestName, null, x.Status, x.RequestedAtUtc, x.UploadedAtUtc,
        x.ReviewedAtUtc, x.RowVersion, count);

    private void Append(string actor, ClinicalTestRequest request, string action) =>
        audit.Append(new(actor, action, "test-request", request.Id.ToString("N"), request.PatientId,
            AuditOutcome.Succeeded, null, null));
}
