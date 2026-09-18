using Clinic.Application.Audit;
using Clinic.Application.Storage;
using Clinic.Domain.Entities;
using Clinic.Domain.Enums;

namespace Clinic.Application.Attachments;

public sealed record UploadAttachmentRequest(
    Guid PatientId,
    Guid? VisitId,
    Stream Content,
    string OriginalFileName,
    string ContentType,
    string ActorStaffId);

public sealed record AttachmentUploadResult(AttachmentUploadError? Error, AttachmentCreated? Attachment)
{
    public bool IsSuccess => Error is null;
}

/// <summary>
/// Upload workflow: authorize/validate cheaply, stream bytes into private storage, then commit
/// StoredFile metadata + PatientAttachment link + the file.upload audit event in ONE shared
/// SaveChanges, so a successful upload is all-or-nothing (object bytes, metadata, link, audit).
/// Failure/compensation behavior:
/// - type/extension/MIME violation or stage/promote failure: no rows, no audit, staged bytes
///   cleaned by the storage provider; nothing visible.
/// - shared save failure: no row committed (single SaveChanges), rejected rows detached, the
///   promoted object deleted as compensation (a failing compensation leaves a documented
///   orphan object, never a dangling attachment), original failure rethrown.
/// - a hard crash between promote and commit can leave an orphaned object with no metadata,
///   link or audit event (reconciliation is a future operational concern).
/// The clinical existence checks are cheap and run BEFORE any byte streams; the doctor
/// authority check for visit-level uploads is an API-boundary responsibility (it needs the
/// persisted staff user) and is enforced there.
/// </summary>
public sealed class AttachmentService(
    IFileStorage storage,
    IAttachmentStore store,
    IAuditMutationWriter auditWriter,
    AttachmentOptions options,
    TimeProvider clock)
{
    public async Task<AttachmentUploadResult> UploadAsync(UploadAttachmentRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Content);
        await using var ownedContent = request.Content;

        // Cheap resource validation first: never stream bytes for a doomed request.
        if (!await store.PatientExistsAsync(request.PatientId, cancellationToken))
            return new(AttachmentUploadError.PatientNotFound, null);
        if (request.VisitId is { } visitId &&
            await store.VisitAsync(request.PatientId, visitId, cancellationToken) is null)
            return new(AttachmentUploadError.VisitMismatch, null);

        // Display filename only: strip any client path components (both separators, on any
        // platform) — it must never influence storage keys, paths or authorization.
        var displayName = request.OriginalFileName.Trim();
        var cut = displayName.LastIndexOfAny(['/', '\\']);
        if (cut >= 0) displayName = displayName[(cut + 1)..].Trim();

        // Narrow feature-level format gate: extension/MIME pairing eagerly, signature while
        // streaming, feature size limit while streaming (storage max is the backstop).
        var type = AttachmentFilePolicy.Resolve(displayName, request.ContentType);
        await using var validated = new ValidatingAttachmentStream(request.Content, type, options.MaxFileSizeBytes, leaveOpen: true);
        var staged = await storage.StageAsync(validated, cancellationToken);
        try
        {
            var storageKey = FileStorageKey.NewStorageKey();
            var file = new StoredFile(storageKey, displayName, type.ContentType,
                staged.SizeBytes, staged.Sha256, request.ActorStaffId, clock.GetUtcNow());
            var attachment = new PatientAttachment(request.PatientId, file.Id, request.VisitId,
                request.ActorStaffId, clock.GetUtcNow());

            await storage.PromoteAsync(staged.StagedKey, storageKey, cancellationToken);
            try
            {
                using var auditScope = auditWriter.BeginMutation();
                store.Add(file, attachment);
                auditWriter.Append(new AuditAppendRequest(request.ActorStaffId, "file.upload", "attachment",
                    attachment.Id.ToString("N"), request.PatientId, AuditOutcome.Succeeded, null, null));
                await store.SaveAsync(cancellationToken);
            }
            catch
            {
                store.DiscardChanges();
                // Nothing was committed (single SaveChanges): compensate the promoted object so
                // no bytes, metadata, link or audit event survive. If this delete also fails an
                // orphan object remains — never a dangling attachment; the original error wins.
                try { await storage.DeleteAsync(storageKey, CancellationToken.None); }
                catch { /* documented orphan-object residual risk */ }
                throw;
            }
            // Scope disposal after a successful save detaches nothing (events are no longer
            // Added); on the failure paths above it discards the rejected event.
            return new(null, new AttachmentCreated(attachment.Id, attachment.PatientId, attachment.VisitId,
                file.OriginalFileName, file.ContentType, file.SizeBytes, file.CreatedAtUtc));
        }
        finally
        {
            // Best-effort staged cleanup: no-op after a successful move; removes the staged
            // object when promotion (or anything after staging) failed.
            try { await storage.DeleteAsync(staged.StagedKey, CancellationToken.None); }
            catch { /* staged cleanup is best-effort */ }
        }
    }
}
