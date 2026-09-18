# Clinical tests Phase 2 — final pre-commit hardening gate

Review scope: existing Phase 2 only. No commit, push, migration application, historical migration
edit, UI work or Phase 3 implementation. Tests use disposable ClinicTests_* databases and private
test storage roots. ClinicDb migration history was queried read-only.

## Findings

| Severity | Finding and disposition |
| --- | --- |
| Critical | None identified. |
| High | None identified. |
| Medium | None identified. |
| Low | Fixed overlapping ownership of the opened result stream. ClinicalTestLifecycleService and the shared attachment upload core both disposed it. The lifecycle service now owns result-upload input exactly once; generic AttachmentService.UploadAsync retains ownership of generic input. The shared core borrows the stream and its validating wrapper leaves it open. |
| Informational | Coverage gaps closed for additional-upload/review races, completion winning over a promoted upload, advancing-clock immutability, standalone reviewer reassociation, exact stream disposal, metadata/count/download permissions, same-patient lower-level rejection, duplicate links and SQL insert constraints. |
| Informational | Same-patient equality is an application/domain invariant, not a cross-table SQL CHECK. Counts are intentionally clinical-test metadata. Orphan-byte reconciliation remains an operational requirement if cleanup fails or a process crashes between promotion and SQL commit. |

## Concurrency evidence

Tests coordinate requests at SavingChanges, after upload promotion and before SQL executes.
Separate request scopes use separate DbContexts. Assertions use fresh database contexts.

- Requested, Uploaded and UnderReview each have a two-upload race with the same token.
  Both reach the save boundary. Exactly one returns 200 and one 409. Only winner metadata,
  attachment/link rows and the two successful upload events remain. Loser objects are deleted.
  A refreshed-token retry succeeds once. UploadedAtUtc is unchanged for additional uploads,
  and UnderReview does not regress.
- Start-review and complete-review races each yield one success and one 409, with exactly one
  corresponding successful audit. Complete contenders use different persisted domain Doctors
  on a standalone request; the persisted reviewer matches the winning actor. A loser retry
  cannot overwrite reviewer/time.
- An upload is held after promotion while complete-review commits using the same starting
  token. Once released, upload returns 409; no new visible result or successful upload audits
  remain, its object is deleted, and the request stays Reviewed.
- SQL RowVersion remains the sole concurrency mechanism. Every result upload marks Status
  modified even when its value is unchanged. No concurrency retries or weaker checks were added.

## Lifecycle and integrity evidence

An advancing TimeProvider test exercises first upload, second upload, start review, another upload,
completion, repeated review commands and late upload. It compares fresh persisted records after
each step. UploadedAtUtc remains the exact first-upload value; final reviewer/time never move.
PatientId, VisitId, RequestedByDoctorId, RequestedAtUtc, Category, TestName and ClinicalInstructions
remain unchanged. PUT/PATCH/DELETE are unavailable. All three mutation responses expose a JSON
base64 string equal to the persisted new eight-byte token; response tokens drive the next command.

Requested -> start, Uploaded -> complete, UnderReview -> start, and all three Reviewed mutations
return 409, leave tokens unchanged and append no successful mutation audit. Domain reflection
tests confirm no public field setter or generic status mutation exists.

SQL uniquely constrains PatientAttachmentId. Duplicate insertion is rejected both for the same
request and for a different request. Multiple independent attachments can belong to one request.
Both foreign keys prohibit cascade deletion. The result-link constructor requires actual request
and attachment entities and rejects unequal PatientIds. A lower-level store test uses persisted
Patient A's order and Patient B's attachment, with an actor granted both patient scopes, and proves
construction/enlistment rejects the link before save and no result appears. The service never
accepts an existing attachment ID: it derives PatientId and VisitId from the loaded order.

SQL guarantees referenced rows exist, uniqueness and NO ACTION deletion, but **does not enforce
cross-table patient equality**. Direct privileged SQL bypassing domain construction remains outside
the application invariant. No claim of a database equality guarantee is made.

The lifecycle CHECK exactly requires:

| Status | UploadedAtUtc | ReviewedAtUtc | ReviewedByDoctorId |
| --- | --- | --- | --- |
| Requested | NULL | NULL | NULL |
| Uploaded | NOT NULL | NULL | NULL |
| UnderReview | NOT NULL | NULL | NULL |
| Reviewed | NOT NULL | NOT NULL | NOT NULL |

Request <= upload <= review ordering is enforced where those times exist. Expanded direct SQL
UPDATE cases test inconsistent null/reviewer/time combinations; direct invalid INSERTs are rejected
for each lifecycle status. Valid states are exercised through the actual lifecycle workflow.

## Security and audit evidence

- tests.read authorizes viewing the request. attachments.read additionally authorizes result
  attachment details. Without it, the collection is null and contains no attachment ID/name/MIME/
  size. resultAttachmentCount is intentionally clinical-test progress metadata and remains visible
  with tests.read on both list and detail. Both clinical roles follow identical rules.
- attachments.read alone cannot view the test request. It independently permits attachment download
  with patient scope, role and MFA, using the existing protected endpoint and file.download audit.
  Knowing an ID provides no bypass of those checks; no storage-key route is added.
- Visit review authority uses live persisted StaffUser.AssociatedDoctorId == Visit.DoctorId.
  Existing-cookie tests change the association before review and verify the old authority is lost.
  Standalone requests require a valid live association; reassociation to another valid Doctor is
  accepted and completion records that Doctor, ignoring supplied reviewer/time/status fields.
- Assistant upload requires tests.write AND attachments.write plus MFA and exact scope, without
  doctor association. Missing each requirement is tested. Forged review fields in the multipart
  upload do not alter lifecycle/reviewer values; both review commands remain forbidden.
- Body-meter tests show missing/invalid CSRF is rejected before request-body reads, StageAsync,
  clinical mutations or upload audits. Review CSRF rejection precedes mutation/audit.
- The existing attachment policy remains the single PDF/JPEG/PNG validation path: MIME/extension,
  streaming magic bytes, preserved prefix bytes, total/feature size limits, private keys/root and
  junction/reparse protections. Repeated bytes get independent objects, without deduplication.
- Every result upload emits exactly one file.upload and one test-request.result.upload. The shared
  attachment service owns the former; the clinical service owns only the latter. One SaveChanges
  transaction commits both with all clinical/file rows. Independent SQL audit-insert failures
  roll back both events and all rows/state/times and compensate the promoted object.
- Review mutation audits are atomic. All four action types have empty metadata; no filenames,
  clinical prose, MIME, hashes, keys or reviewer IDs are copied into metadata.
- List/detail access audits remain fail-closed. Failure prevents both request and attachment DTO
  disclosure. No partially populated response escapes.
- Stream tests require exactly one disposal on early validation, stale token, staging failure,
  audit failure, cancellation after promotion and success. Framework Request.Body/IFormFile objects
  are not disposed by application code; only the opened file stream is owned by the service.
- If object compensation fails after SQL failure, tests verify the original SQL error remains
  primary, the response is sanitized, and there are no visible DB rows/events. Unreferenced bytes
  may remain. An operational orphan-object reconciliation procedure is still required; it is not
  implemented as part of this gate.

## Migration review and pending list

`20260918114928_AddClinicalTestResultsAndLifecycle` contains only the new result-link table, its
intended FKs/indexes and replacement of CK_ClinicalTestRequests_Lifecycle. It does not alter the
StoredFiles, PatientAttachments, AuditEvents, Prescriptions, Appointments, Identity or Medications
schemas. The Phase 1 historical migration is unchanged. No new migration was generated by this gate.

Current pending migrations, none applied:

1. 20260915105237_AddPatientMedicalRecordFoundation
2. 20260915192837_AddVisitsAndVitalMeasurements
3. 20260915201721_AddMedicationCatalogAndPrescriptions
4. 20260916161906_AddAuditFoundation
5. 20260916214417_AddFileStorageFoundation
6. 20260916224607_AddPatientAttachments
7. 20260917034049_AddClinicalTestRequests
8. 20260918114928_AddClinicalTestResultsAndLifecycle

## Gate changes

Paths are relative to the repository root; these are the changes made during this gate, separate
from the already-uncommitted Phase 2 implementation.

| File | Reason |
| --- | --- |
| backend/Clinic/src/Clinic.Application/Attachments/AttachmentService.cs | Assign one input-stream owner per public workflow; shared core borrows input. |
| backend/Clinic/tests/Clinic.IntegrationTests/Api/StaffClinicalTestHardeningTests.cs | New deterministic race, immutability, permission, same-patient, standalone, assistant and disposal tests. |
| backend/Clinic/tests/Clinic.IntegrationTests/Api/StaffClinicalTestResultFailuresTests.cs | Add reusable pre-save coordination hook and optional test clock. |
| backend/Clinic/tests/Clinic.IntegrationTests/Api/StaffClinicalTestResultsHttpTests.cs | Complete assistant missing-grant/MFA/scope cases and assert read-audit failure also hides clinical data. |
| backend/Clinic/tests/Clinic.IntegrationTests/ClinicalTests/ClinicalTestResultPersistenceTests.cs | Duplicate link to same/different request; expanded invalid SQL states and direct INSERT checks. |
| docs/clinical-tests-phase-2.md | Clarify count classification, independent download authority and final-gate references. |
| docs/progress-2026-09-18.md | Record final-gate outcome and verification. |
| docs/clinical-tests-phase-2-final-gate.md | This review/evidence report. |

## Verification

**READY TO COMMIT CLINICAL TESTS PHASE 2**

- `git diff --check`: passed.
- `dotnet build backend/Clinic/Clinic.slnx --nologo -m:1 -p:UseSharedCompilation=false`:
  passed, zero warnings and zero errors.
- `dotnet test backend/Clinic/Clinic.slnx --no-build --no-restore --nologo -m:1 --verbosity minimal`:
  **847 passed — 269 unit + 578 integration, zero failed, zero skipped**. The gate added 42
  integration cases to the prior 805-test baseline. The initial focused selection passed 43 cases.
- `dotnet ef migrations has-pending-model-changes --no-build --project backend/Clinic/src/Clinic.Infrastructure --startup-project backend/Clinic/src/Clinic.Api`:
  no changes to the model since the last migration.
- `dotnet ef migrations list --project backend/Clinic/src/Clinic.Infrastructure --startup-project backend/Clinic/src/Clinic.Api`:
  succeeded; all eight listed migrations remain pending. ClinicDb schema/data untouched.

No unresolved blocking findings. The application invariant, deliberate count visibility and
residual orphan-byte limitation are explicitly documented above.

Next if ready: **CLINICAL TESTS PHASE 3 — STRUCTURED RESULT/REVIEW PLANNING ONLY**.
Do not implement Phase 3. No commit or push has been performed.
