# Doctor Tablet Phase 1A-4B

## API and payload contract

`POST /api/staff/patients/{patientId}/notebook/pages/{pageId}/revisions`

Multipart fields (exactly three single-valued fields and one file):

- `expectedRowVersion`: base64 SQL rowversion, exactly 8 decoded bytes.
- `clientDraftId`: required, trimmed, maximum 128 characters, no control characters; unique within the page.
- `originDeviceId`: required, trimmed, maximum 128 characters, no control characters. This is untrusted metadata, not device authentication.
- `payload`: one UTF-8 JSON file, maximum 16 KiB of actual bytes. Client filename and declared MIME type do not determine the private key or persisted filename/content type.

The only accepted JSON fields are:

```json
{"formatVersion":1,"patientId":"<route patient GUID>","pageId":"<route page GUID>"}
```

Unknown, duplicate or missing JSON fields, unsupported versions, invalid JSON and ownership mismatches are rejected. The database page is looked up by both route IDs. No payload identifier can redefine ownership. Request size is bounded to 32 KiB including multipart overhead; individual parts/values/headers and actual payload bytes also have limits. Malformed/oversized multipart returns a sanitized 400 or 413; streamed payload overflow returns 413.

New revision: **201**, Location points to its protected payload. Exact retry: **200**. Response fields are `revisionId`, `revisionNumber`, `rowVersion` (the current page version), `replayed`. No storage keys, URLs or raw draft/device metadata are returned.

The Phase 1A-4A creation event remains revision 1. Therefore the first payload revision is **2**, then 3, etc. Earlier revisions remain unchanged.

`GET /api/staff/patients/{patientId}/notebook/pages/{pageId}/revisions/{revisionNumber}/payload`

Returns a private `application/json` streamed attachment with `no-store` and `nosniff`. Server-side lookup includes patient, page and revision. Creation-only revision 1 has no payload and returns 404. Missing/corrupt objects return 503. Reads buffer at most 16 KiB for checksum, size and ownership validation before the fail-closed read audit; verified bytes are then streamed. Storage keys never appear in the response.

Existing cookie/mobile selector, Identity/MFA/session validation, persisted notebook permissions, and exact patient scope checks are reused. Doctor can upload/read with applicable permissions; DoctorAssistant can only read; Receptionist is denied even with notebook claims. Cookie writes require header CSRF validation before multipart parsing. Existing default ClinicStaff cookie authentication is unchanged.

## Storage and transaction workflow

1. Authorize and resolve page; bound incoming bytes.
2. Reuse `IFileStorage.StageAsync` to stage private bytes and compute SHA-256 and exact size.
3. Validate the JSON contract and ownership against route IDs.
4. Start a database transaction and acquire the existing SQL `UPDLOCK, ROWLOCK` pattern on the page. Recheck page ownership and idempotency, then expected rowversion.
5. Stage a new immutable revision, StoredFile and `notebook.revision.save` audit event; update page revision counter/time with expected rowversion. Save inside the still-uncommitted transaction.
6. Promote to a server-generated opaque `clinic-files/{GUID}` key, then commit. No revision is visible to a reader before commit.
7. On failure, transaction disposal rolls back rows; delete staged bytes and compensate promoted bytes. Commit exceptions are potentially ambiguous: a fresh context checks whether the revision committed before deleting a promoted object. If that verification cannot complete, bytes are retained rather than risking deletion of a committed payload.

StoredFile persists SHA-256, measured size, fixed `notebook.json` filename, fixed application/json type, server actor and server time. Existing private-root, key containment and non-public storage behavior are reused. All audit events omit payload/title and raw client metadata. Reads audit `notebook.revision.read` only after integrity checks; audit failure prevents disclosure.

## Idempotency and concurrency

Within the page transaction, an existing ClientDraftId is checked **before** expected rowversion. Matching exact bytes (SHA-256 + length), normalized device ID and authenticated staff author returns the original immutable revision with the current page rowversion, even if the original expected version is stale. It creates no file, revision or duplicate save-audit event. Different bytes (including JSON whitespace), device, author or a creation-only draft key produce **409 `draft_conflict`**. Database collation aliases are not accepted as exact retries: the stored draft string must match ordinally.

A new draft with stale expectedRowVersion produces **409 `page_changed`**. There is no merge or overwrite. The SQL page lock serializes writers across app instances; same-draft races create one revision and one replay response. Different-draft races using the same expected version yield one success and one conflict. Existing unique page/revision and page/draft indexes remain final database guards. EF rowversion remains the optimistic concurrency token.

## Database

Additive migration: `20260919175233_AddNotebookRevisionPayload`.

- Nullable `NotebookRevisions.StoredFileId` for backward-compatible creation metadata.
- Restrictive FK to StoredFiles, filtered unique StoredFileId index.
- Check constraint requires a StoredFile and draft/device identifiers for Payload revisions; Created revisions have no StoredFile.
- `NotebookRevisionKind.Payload = 1`; existing Created value remains 0.
- All EF SaveChanges paths now reject modification/deletion of existing NotebookRevision rows, following the existing append-only audit guard. Privileged direct SQL is outside that application guarantee.

Migration generated only. No migration was applied to main ClinicDb. Integration fixtures use disposable `ClinicTests_*` SQL databases and private temporary file roots.

## Files changed in this slice

- Domain: `Entities/NotebookPage.cs`, `Entities/NotebookRevision.cs`, `Enums/NotebookEnums.cs`.
- Application: `Notebook/INotebookStore.cs`, new `Notebook/NotebookPayloadService.cs`.
- Infrastructure: `Repositories/NotebookStore.cs`, `Persistence/NotebookMapping.cs`, `Persistence/ClinicDbContext.cs`, payload migration/designer and model snapshot.
- API: `Controllers/StaffNotebookPagesController.cs`, new `Notebook/NotebookMultipartAttribute.cs`, `Controllers/AttachmentUploadGateAttribute.cs` (reuse existing bounded request stream), `Program.cs` (service registration).
- Tests: new `Api/StaffNotebookPayloadHttpTests.cs`, `Api/StaffNotebookPayloadFailureTests.cs`, `NotebookPayloadTests.cs`.
- This document. Existing earlier-phase working-tree changes were preserved.

## Verification

Final verification on 2026-09-20:

- `dotnet build backend/Clinic/Clinic.slnx`: passed, zero warnings/errors.
- `dotnet test backend/Clinic/Clinic.slnx --no-build` (with TRX result logging): 279 unit and 703 integration tests passed; zero failures/skips.
- `dotnet ef migrations has-pending-model-changes` with Infrastructure/Api projects and `--no-build`: no pending model changes.
- `git diff --check`: passed (only Git line-ending conversion notices).
- New coverage includes successful revision history, exact/conflicting retries, synchronized concurrent submissions, stale rowversion, scope and payload tampering, role restrictions, CSRF/size validation, private integrity-checked reads, fail-closed auditing, staging/promotion/database/commit compensation, and lost commit acknowledgements that must preserve committed bytes.

## Operational limits and deferred work

There is no distributed transaction across SQL and the filesystem. A process crash or failed cleanup may leave an unreferenced private object; an unavailable commit-verification query deliberately retains objects. Automatic reconciliation/cleanup is not added. Per-page transactions hold a lock during bounded promotion. Offset clocks are server-authoritative, but not a replacement for monotonic revision numbers.

Page creation remains non-idempotent as in 4A; only revision upload receives idempotent retry behavior. Devices are not bound to identities. Clients must retain the exact payload bytes and draft/device IDs for retries. Draft IDs used on creation are already reserved in that page's revision namespace.

No real stroke geometry, MessagePack, finalize/amend, Flutter, encrypted local drafts, offline queues, conflict UI, PDF or export is implemented.
