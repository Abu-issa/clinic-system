# Doctor Tablet Phase 1A-4C: finalization and amendments

## Lifecycle and contracts

`POST /api/staff/patients/{patientId}/notebook/pages/{pageId}/finalize`

JSON body: `{ "expectedRowVersion": "<base64 SQL rowversion>" }`. Exactly 8 decoded bytes are required. The response is 200 with the existing page-detail projection, including current rowversion and immutable finalization fields. Client fields cannot override the associated doctor, actor, time or ownership.

The authenticated Identity account's AssociatedDoctorId becomes FinalizedByDoctorId; the server TimeProvider supplies FinalizedAtUtc and UpdatedAtUtc. The first successful finalize changes rowversion and writes one `notebook.page.finalize` audit in the same transaction. It does not create a revision or change CurrentRevisionNumber. Finalizing a creation-only page is permitted; no minimum payload count was introduced.

An unfinalized page requires the current version; stale versions return 409 `page_changed`. A repeated finalize of an already-finalized page returns its current state, even with the original stale version, without modifying timestamps, actor, rowversion or audit history. The request must still contain a valid 8-byte version and pass current authorization and associated-doctor checks. This handles simultaneous finalizations and lost commit acknowledgements safely. Another authorized Doctor's repeat does not replace the original finalizing doctor.

`POST /api/staff/patients/{patientId}/notebook/pages/{pageId}/amendments`

Uses the same bounded multipart contract as 4B: expectedRowVersion, clientDraftId, originDeviceId, and payload. Payload remains the exact three-field version-1 JSON contract (`formatVersion`, `patientId`, `pageId`), with a 16 KiB payload limit and 32 KiB total request limit. IDs must match the authoritative route and database page.

Before finalization, normal Payload revisions are permitted and amendments return 409 `invalid_lifecycle`. After finalization, new and retried normal uploads return 409 `invalid_lifecycle`; amendments are permitted only through the amendments endpoint. Rejecting a late normal retry does not delete its already-committed earlier revision. Clients can still read it through the protected payload endpoint.

An amendment appends one immutable `Kind = Amendment` revision, increments CurrentRevisionNumber, and changes UpdatedAtUtc and RowVersion. It never changes FinalizedAtUtc or FinalizedByDoctorId. Response: 201 for creation, 200 for exact replay, with revisionId, revisionNumber, current page rowVersion and replayed. Location uses the existing protected revision payload route. No storage keys or raw draft/device metadata are returned.

Idempotency remains per page, with exact payload bytes/checksum/length, normalized draft/device IDs, authenticated author, and now revision **kind** all checked. A Payload draft cannot be replayed as an Amendment. Conflicting draft reuse returns 409 `draft_conflict`; a new amendment with stale expectedRowVersion returns 409 `page_changed`. Exact amendment retries are evaluated before rowversion and produce no duplicate file, revision, or audit.

## Authorization, audit, storage and concurrency

Existing ClinicNotebookStaff cookie/mobile selection, Identity/MFA/revocation validation, NotebookWrite policy, persisted notebook.write and exact patient scopes remain authoritative. Doctor only can finalize/amend; DoctorAssistant and Receptionist cannot. Cookie writes retain CSRF checks. Cross-patient page IDs return 404 under an authorized different patient, and out-of-scope patients return 403. No body-supplied actor or ownership ID is accepted.

All three writes (normal revision, finalize, amendment) acquire the existing transaction-owned SQL page update lock. State and rowversion checks occur under that lock. Finalize versus normal revision yields one success and one conflict for the same expected version; repeated concurrent finalize calls converge on one finalization/audit. Different concurrent amendment drafts yield one success and one stale conflict; duplicate amendment drafts yield one creation and one replay. SQL unique indexes and EF rowversion remain in force. No merge is performed.

Amendments reuse the 4B stage → validate → transaction/save → promote → commit workflow, including cleanup, compensation and fresh-context verification of ambiguous commits. `notebook.revision.amend` replaces `notebook.revision.save` for amendments. Audit events contain no payload, title, tokens or raw client metadata. Existing fail-closed, integrity-checked protected payload reads cover all committed payload/amendment revisions unchanged.

Domain methods expose no unfinalize/refinalize mutation, and all EF SaveChanges paths additionally reject changing existing finalization metadata, alongside the existing append-only revision guard. These application guards do not protect against privileged direct SQL changes.

## Migration

Generated `20260920104620_AddNotebookAmendmentKind` changes only `CK_NotebookRevisions_Payload` to accept Kind 2 with the same required StoredFile/draft/device constraints as Kind 1. No new tables/columns or data deletion. Down migration cannot be used while amendment rows exist because the older constraint cannot represent them. No migrations were applied to main ClinicDb; integration fixtures use disposable ClinicTests databases and private temporary storage.

## Files changed for 4C

- Domain: NotebookPage.cs, NotebookRevision.cs, NotebookEnums.cs.
- Application/Notebook: NotebookService.cs, NotebookPayloadService.cs, NotebookModels.cs, INotebookStore.cs.
- Infrastructure: NotebookStore.cs, NotebookMapping.cs, ClinicDbContext.cs; amendment-kind migration/designer and ClinicDbContextModelSnapshot.cs.
- API: StaffNotebookPagesController.cs and Notebook/NotebookModels.cs.
- Tests: new StaffNotebookLifecycleHttpTests.cs and NotebookLifecycleTests.cs; extended StaffNotebookPayloadHttpTests.cs upload helper and StaffNotebookPayloadFailureTests.cs failure harness/coverage.
- This document. Earlier-phase working-tree changes were preserved.

## Verification

On 2026-09-20, `dotnet build backend/Clinic/Clinic.slnx` passed with zero warnings/errors. `dotnet test backend/Clinic/Clinic.slnx --no-build` (with TRX logging) passed **281 unit and 718 integration tests**, zero failures/skips. EF `migrations has-pending-model-changes` reported none. `git diff --check` passed with only line-ending conversion notices.

Focused coverage includes finalization and stale/no-op retry behavior, authorization/scope/CSRF and ownership, lifecycle restrictions, immutable finalization and revision history, kind-aware amendment retries/conflicts, synchronized races, audit correctness, rollback on audit/commit failures, lost commit acknowledgement, and amendment staging/promotion/database/commit compensation.

## Deferred work and operational limits

No Flutter, stylus, geometry, encrypted drafts, offline queue, conflict UI, export/PDF, page editing/deletion or reconciliation job. The existing 4B crash/failed-cleanup residual risk remains: SQL and filesystem are not a distributed transaction, so an unreferenced private object may require future reconciliation. Finalization is a permanent workflow state, not a digital signature or legal attestation feature.
