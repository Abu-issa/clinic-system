using System.Text.Json;
using Clinic.Application.Audit;
using Clinic.Application.Exceptions;
using Clinic.Application.Storage;
using Clinic.Domain.Entities;
using Clinic.Domain.Enums;

namespace Clinic.Application.Notebook;

public sealed record NotebookSaveResult(string? Error, Guid? RevisionId = null, long? RevisionNumber = null,
    byte[]? RowVersion = null, bool Replayed = false);

public sealed class NotebookPayloadService(INotebookStore store, IFileStorage storage,
    IAuditMutationWriter audit, TimeProvider clock)
{
    public const int MaxBytes = 16384;

    public static bool ValidPayload(byte[] bytes, Guid patientId, Guid pageId)
    {
        try
        {
            using var doc = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 4 });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            var fields = root.EnumerateObject().ToArray();
            return fields.Length == 3 && fields.Select(x => x.Name).Distinct().Count() == 3 &&
                root.TryGetProperty("formatVersion", out var v) && v.TryGetInt32(out var version) && version == 1 &&
                root.TryGetProperty("patientId", out var patient) && patient.ValueKind == JsonValueKind.String &&
                patient.TryGetGuid(out var p) && p == patientId &&
                root.TryGetProperty("pageId", out var page) && page.ValueKind == JsonValueKind.String &&
                page.TryGetGuid(out var id) && id == pageId;
        }
        catch (JsonException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    public static async Task<byte[]> ReadBoundedAsync(Stream input, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var bytes = new byte[4096];
        int count;
        while ((count = await input.ReadAsync(bytes, ct)) != 0)
        {
            if (buffer.Length + count > MaxBytes) throw new FileSizeLimitExceededException(MaxBytes);
            buffer.Write(bytes, 0, count);
        }
        return buffer.ToArray();
    }

    public async Task<NotebookSaveResult> SaveAsync(Guid patientId, Guid pageId, byte[] expectedVersion,
        string draft, string device, string actor, Stream input, CancellationToken ct, bool amendment = false)
    {
        if (expectedVersion.Length != 8 || !ValidIdentifier(draft) || !ValidIdentifier(device)) return new("invalid_input");
        draft = draft.Trim(); device = device.Trim();
        if (await store.GetPageAsync(patientId, pageId, ct) is null) return new("page_not_found");
        var bytes = await ReadBoundedAsync(input, ct);
        await using var source = new MemoryStream(bytes, writable: false);
        var staged = await storage.StageAsync(source, ct);
        string? promotedKey = null;
        Guid? revisionId = null;
        bool commitAttempted = false, committed = false;
        using var auditScope = audit.BeginMutation();
        try
        {
            if (!ValidPayload(bytes, patientId, pageId)) return new("invalid_payload");
            await using var tx = await store.BeginRevisionAsync(patientId, pageId, ct);
            var page = tx.Page;
            if (page is null) return new("page_not_found");
            // State is evaluated under the same page lock as finalize. Normal uploads (including
            // their retries) cannot pass the finalized boundary; amendments use their own endpoint.
            if (amendment != (page.FinalizedAtUtc is not null)) return new("invalid_lifecycle");
            var kind = amendment ? NotebookRevisionKind.Amendment : NotebookRevisionKind.Payload;
            var previous = await store.DraftAsync(pageId, draft, ct);
            if (previous is not null)
            {
                if (previous.Revision.Kind != kind || previous.File?.Sha256 != staged.Sha256 || previous.File.SizeBytes != staged.SizeBytes ||
                    previous.Revision.ClientDraftId != draft || previous.Revision.OriginDeviceId != device ||
                    previous.Revision.AuthorStaffId != actor) return new("draft_conflict");
                return new(null, previous.Revision.Id, previous.Revision.RevisionNumber, page.RowVersion.ToArray(), true);
            }
            if (!page.RowVersion.SequenceEqual(expectedVersion)) return new("page_changed");
            var key = FileStorageKey.NewStorageKey();
            var file = new StoredFile(key, "notebook.json", "application/json", staged.SizeBytes,
                staged.Sha256, actor, clock.GetUtcNow());
            var revision = amendment ? page.AppendAmendment(file.Id, actor, clock.GetUtcNow(), draft, device)
                : page.AppendPayload(file.Id, actor, clock.GetUtcNow(), draft, device);
            revisionId = revision.Id;
            store.AddPayload(file, revision, page, expectedVersion);
            audit.Append(new(actor, amendment ? "notebook.revision.amend" : "notebook.revision.save", "notebook-revision", revision.Id.ToString("N"),
                patientId, AuditOutcome.Succeeded, null, null));
            await store.SaveRevisionAsync(ct);
            await storage.PromoteAsync(staged.StagedKey, key, ct);
            promotedKey = key;
            commitAttempted = true;
            await tx.CommitAsync(ct);
            committed = true;
            return new(null, revision.Id, revision.RevisionNumber, page.RowVersion.ToArray());
        }
        catch (PersistenceConcurrencyException) { return new("page_changed"); }
        finally
        {
            // Transaction disposal precedes compensation. An ambiguous commit must be checked
            // from a fresh context; if verification fails, retain bytes rather than delete committed data.
            if (!committed && promotedKey is not null)
            {
                var mayDelete = !commitAttempted;
                if (commitAttempted)
                    try { mayDelete = !await store.RevisionCommittedAsync(revisionId!.Value, CancellationToken.None); }
                    catch { mayDelete = false; }
                if (mayDelete) try { await storage.DeleteAsync(promotedKey, CancellationToken.None); } catch { }
            }
            store.DiscardChanges();
            try { await storage.DeleteAsync(staged.StagedKey, CancellationToken.None); } catch { }
        }
    }

    private static bool ValidIdentifier(string value) => !string.IsNullOrWhiteSpace(value) &&
        value.Trim().Length <= 128 && !value.Any(char.IsControl);
}
