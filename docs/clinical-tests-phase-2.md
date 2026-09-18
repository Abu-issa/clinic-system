# Clinical tests — Phase 2

Scope: result attachment upload and controlled lifecycle only. Phase 3 and UI are not implemented.

## Result model and lifecycle

`ClinicalTestResultAttachment` contains `Id`, `ClinicalTestRequestId`, `PatientAttachmentId`,
and UTC `LinkedAtUtc`. Both foreign keys use Restrict/SQL NO ACTION. The unique
`PatientAttachmentId` index prevents sharing one attachment between orders; an index on
`ClinicalTestRequestId, LinkedAtUtc` supports result retrieval. Its constructor accepts the
actual request and attachment and rejects mismatched patients. SQL foreign keys enforce
existence; same-patient ownership is enforced by the domain and specialized workflow rather
than a cross-table SQL CHECK. No API accepts an existing attachment ID to link.

Each upload creates a new StoredFile, PatientAttachment and result link. It sets the attachment's
patient and optional visit directly from the request. Metadata stays on StoredFile/PatientAttachment.
Repeated identical bytes are allowed with independent IDs and storage keys; no hash deduplication.

| Command | Required state | Result |
| --- | --- | --- |
| First result upload | Requested | Uploaded; first upload time recorded |
| Additional result upload | Uploaded | Uploaded; first upload time preserved |
| Additional result upload | UnderReview | UnderReview; first upload time preserved |
| Start review | Uploaded | UnderReview |
| Complete review | UnderReview | Reviewed; server reviewer and review time recorded |

All other transitions return 409. Reviewed is immutable: no more uploads, restarts, regression,
arbitrary editing, unreview or deletion. There is no generic status setter. `UploadedAtUtc` is
the TimeProvider UTC time of the first successfully committed result; later uploads preserve it.
`ReviewedAtUtc` is TimeProvider UTC and `ReviewedByDoctorId` is the completing actor's live
persisted AssociatedDoctorId. No review-start timestamp or interpretation notes are added.

## API and authorization

Base: `/api/staff/patients/{patientId}/test-requests/{requestId}`.

| POST suffix | Body | Authorization |
| --- | --- | --- |
| `/results` | Multipart: exactly one `file`, one base64 `expectedRowVersion` | Doctor or DoctorAssistant; tests.write AND attachments.write |
| `/review/start` | JSON: `expectedRowVersion` | Doctor; tests.write |
| `/review/complete` | JSON: `expectedRowVersion` | Doctor; tests.write |

All operations require authenticated staff, MFA, exact patient_record_id scope and CSRF.
Receptionists are denied. The existing session validator revalidates persisted role/permissions/
scope on every request; removing a grant invalidates the existing session immediately.

Doctors require a non-null persisted doctor association. For visit-linked orders it must equal
the persisted Visit.DoctorId. Client doctor IDs and appointment/schedule authority do not enter
this decision. Assistants can upload without doctor association, but cannot start or complete review.

The attachment resource gate validates header-only `X-CSRF-TOKEN`, both upload permissions,
patient/request ownership, Doctor authority and upload state before reading multipart bytes.
Explicit form parsing under the existing bounded stream preserves 413 for total request limit
violations (automatic MVC form binding otherwise converts stream-size exceptions to 400).
The API owns IFormFile; the application receives only Stream. RowVersion is validated before
storage staging. No file is staged for stale tokens.

Malformed input returns 400, authorization failure 403, hidden patient/request mismatch 404,
state/concurrency conflict 409, excessive size 413 and unsupported type 415. Infrastructure
failures use the existing sanitized error handler. Errors disclose no physical paths, SQL details,
stack traces, storage keys or clinical filenames.

## Concurrency and responses

Uses the existing SQL eight-byte RowVersion and standard JSON byte-array/base64 convention.
Multipart carries the same base64 encoding. Protected detail exposes `rowVersion`; each successful
mutation returns the new token, status, patient/visit/category/name, request/upload/review times
and result count. Staff and reviewer IDs are not exposed. Create retains its existing lightweight
response; clients can obtain the first token through detail.

Every upload explicitly marks the request status column modified, even when the status value and
UploadedAtUtc are unchanged. This ensures SQL UPDATE checks the expected RowVersion and issues
a new token for every result. There is no second concurrency mechanism or automatic retry.
Concurrent uploads with the same token yield exactly one success; the loser rolls back all new
rows/events, deletes the promoted object, and returns 409. Refresh detail before retrying.
Mutation responses are built from the committed tracked entity without another cancellable query.

## File security, transaction and compensation

The specialized operation reuses AttachmentService's existing upload core through an application-
internal enlistment callback. It does not call the generic upload followed by a separate link/save.
Both stores and the audit writer share the scoped ClinicDbContext. The callback only stages
the result link, request update and result audit event; AttachmentService owns the single save.

The existing AttachmentFilePolicy, streaming signature/size validator, filename normalization,
IFileStorage, generated private keys, provider root containment and reparse/junction protections
remain authoritative. PDF, JPEG and PNG only; extension, MIME and magic bytes must agree.
Feature limit defaults to 10 MiB with the existing configured storage backstop and multipart
overhead allowance. Neither application workflow buffers the full file into a byte array.

Flow: authorize and check token; stage/validate bytes; create file and attachment metadata;
promote object; enlist request update, result link and both audits; one SaveChanges transaction.
Any failed SQL/audit insert rolls back every database mutation. AttachmentStore detaches the
dependent PatientAttachment before StoredFile so cleanup does not mask a concurrency exception
after EF relationship fixup. Rejected clinical rows and unsaved audit events are also detached.
Promoted-object and staging cleanup use CancellationToken.None.

On stage, signature, size, promotion or cancellation failure, no clinical result becomes visible.
If promoted-object deletion itself fails, the original operation failure remains primary and
unreferenced bytes may remain; no database attachment/result is visible. A hard crash between
promotion and commit has the same residual orphan-object risk. There is no distributed transaction
claim or automatic reconciliation/retention feature in this phase.

## Audits and protected reads

| Action | Resource type / ID | When |
| --- | --- | --- |
| file.upload | attachment / PatientAttachment.Id | Result upload |
| test-request.result.upload | test-request / ClinicalTestRequest.Id | Same result upload transaction |
| test-request.review.start | test-request / ClinicalTestRequest.Id | Start review transaction |
| test-request.review.complete | test-request / ClinicalTestRequest.Id | Complete review transaction |

All four have PatientId, actual ActorStaffId, Succeeded outcome and EMPTY metadata. No filename,
test name, instructions, MIME, hash, key or interpretation is placed in audit metadata. Upload's
two events are intentional distinct semantic events; either insert failure rolls back both.
Denials, invalid states, stale tokens and failed uploads create no Succeeded event. Failed-attempt
auditing is not introduced. Existing audit.patient.read can query these patient-scoped events;
AuditEvents schema is unchanged.

Detail requires tests.read and uses one fail-closed test-request.read event, including disclosure
of permitted result metadata. With attachments.read, `resultAttachments` contains attachmentId,
originalFileName, contentType, sizeBytes and createdAtUtc. Without it, that collection is null.
Counts remain visible with tests.read. Lists include times/counts and never full attachment arrays.
This is intentional: resultAttachmentCount is clinical-test progress metadata, like lifecycle
status, rather than permission to enumerate attachments. It reveals only the number of linked
results within the authorized patient's request. Without attachments.read no attachment ID,
filename, MIME or size is returned. The same rule applies to Doctors and DoctorAssistants.
No StoredFileId, StorageKey, Sha256, provider, path or staff ID is exposed.

Bytes use only the existing `/api/staff/patients/{patientId}/attachments/{attachmentId}/download`
route, its attachments.read/MFA/scope/role checks, attachment disposition, nosniff and fail-closed
file.download audit. There are no direct object URLs or new download mechanism. All request
and attachment metadata queries retain explicit patient predicates.
Download authorization is independent: attachments.read with patient scope/role/MFA permits
downloading an attachment even without tests.read. That does not permit viewing its test request.

## Migration and verification

Exactly one migration: `20260918114928_AddClinicalTestResultsAndLifecycle`.
Adds ClinicalTestResultAttachments with its two Restrict foreign keys and indexes; replaces only
`CK_ClinicalTestRequests_Lifecycle`. Requested requires all result/reviewer fields null;
Uploaded/UnderReview require upload time and null reviewer/time; Reviewed requires all three.
It also enforces request <= upload <= review chronology where applicable. Category validation
is preserved. The historical `20260917034049_AddClinicalTestRequests` migration is unchanged.
No existing attachment/file/audit/patient/visit/identity schema is changed. Migration NOT applied
to ClinicDb. Test fixtures migrate and destroy only disposable ClinicTests_* databases.

Coverage includes domain state/time/identity/Unicode/DTO invariants; SQL link uniqueness,
NO ACTION FKs and invalid lifecycle states; full multi-file HTTP lifecycle, byte-exact existing
downloads, roles/MFA/scope/live authority, grant revocation, CSRF before body reads, file policy,
request/feature limits, hidden cross-patient IDs, metadata redaction, read audit failures, stale
tokens and refreshed retries, coordinated two-context SQL race, each upload audit/metadata/link
insert failure, review audit rollback, storage failures, cancellation and failed compensation.

Final hardening verification: **847 passed (269 unit + 578 integration), zero failures/skips**; build clean
with zero warnings/errors; EF drift clean; diff whitespace check clean. The migration remains
pending in ClinicDb. Exact commands and results are recorded in `clinical-tests-phase-2-final-gate.md`
and `progress-2026-09-18.md`. Verdict: **READY TO COMMIT CLINICAL TESTS PHASE 2**.

## Changed file inventory

Paths below are relative to the repository root. The original Phase 2 inventory below is supplemented
by `backend/Clinic/tests/Clinic.IntegrationTests/Api/StaffClinicalTestHardeningTests.cs` and
`docs/clinical-tests-phase-2-final-gate.md` during the final hardening gate.

| Area | Files |
| --- | --- |
| API | `backend/Clinic/src/Clinic.Api/Controllers/AttachmentUploadGateAttribute.cs`; `backend/Clinic/src/Clinic.Api/Controllers/StaffClinicalTestsController.cs`; `backend/Clinic/src/Clinic.Api/Program.cs` |
| Application | `backend/Clinic/src/Clinic.Application/Attachments/AttachmentService.cs`; `backend/Clinic/src/Clinic.Application/ClinicalTests/ClinicalTestModels.cs`; new `backend/Clinic/src/Clinic.Application/ClinicalTests/ClinicalTestLifecycleService.cs` |
| Domain | `backend/Clinic/src/Clinic.Domain/Entities/ClinicalTestRequest.cs`; new `backend/Clinic/src/Clinic.Domain/Entities/ClinicalTestResultAttachment.cs` |
| Persistence | `backend/Clinic/src/Clinic.Infrastructure/Persistence/ClinicalTestMapping.cs`; `backend/Clinic/src/Clinic.Infrastructure/Repositories/ClinicalTestStore.cs`; `backend/Clinic/src/Clinic.Infrastructure/Repositories/AttachmentStore.cs` |
| Migration | new `backend/Clinic/src/Clinic.Infrastructure/Persistence/Migrations/20260918114928_AddClinicalTestResultsAndLifecycle.cs` and `.Designer.cs`; `backend/Clinic/src/Clinic.Infrastructure/Persistence/Migrations/ClinicDbContextModelSnapshot.cs` |
| Integration tests | new `backend/Clinic/tests/Clinic.IntegrationTests/Api/StaffClinicalTestResultsHttpTests.cs`; new `backend/Clinic/tests/Clinic.IntegrationTests/Api/StaffClinicalTestResultFailuresTests.cs`; new `backend/Clinic/tests/Clinic.IntegrationTests/ClinicalTests/ClinicalTestResultPersistenceTests.cs`; `backend/Clinic/tests/Clinic.IntegrationTests/Api/StaffClinicalTestsHttpTests.cs`; `backend/Clinic/tests/Clinic.IntegrationTests/Api/StaffAttachmentRequestGateTests.cs` (share body-meter helper) |
| Unit tests | new `backend/Clinic/tests/xUnit Test Project/ClinicalTestLifecycleTests.cs`; `backend/Clinic/tests/xUnit Test Project/ClinicalTestDomainTests.cs` |
| Documentation | new `docs/clinical-tests-phase-2.md`; new `docs/progress-2026-09-18.md` |

## Remaining scope

No structured lab values, result interpretation notes, test catalog, reference ranges, DICOM/PACS,
patient result upload, notifications, external lab integration, HL7/FHIR, OCR, Flutter, Staff MVC UI,
Archive/OCR or Doctor Notebook. Retention, malware scanning/deep file inspection, orphan-object
reconciliation and amendments are separate designs. Phase 3 remains unimplemented; its scope
must follow the approved master requirements rather than inferring new fields or integrations.

Final hardening evidence and verification are recorded in `clinical-tests-phase-2-final-gate.md`.
No commit or push performed. Phase 3 remains planning only and has not been started.
