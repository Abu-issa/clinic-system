 File Storage Phases 1+2 — final pre-commit hardening gate

Date: 2026-09-17. HEAD: f548588. No commit, push, staging, reset, restore, amend, or ClinicDb migration application performed.
#
## 1. FINAL VERDICT

**READY TO COMMIT FILE STORAGE PHASES 1+2**

All requested correctness/security checks pass after the fixes below. This is a source-control readiness verdict, not a deployment/migration approval.

## 2. FINDINGS BY SEVERITY

- Critical: NONE.
- High, FIXED: ValidatingAttachmentStream lost remaining signature-prefix bytes when the destination buffer was smaller than the buffered prefix. Corrected prefix draining; genuine one-byte/non-seekable and exact byte/hash/size tests added.
- Medium, FIXED: upload request limit was a fixed number rather than configuration-driven, with framework multipart buffering preceding application checks. Added per-endpoint total actual-byte cap and multipart limits, with CSRF/patient/visit authority before model binding.
- Medium, FIXED: public-root validation allowed LocalRoot == wwwroot and skipped checking when the web root was absent. Equality, descendants, normalized paths, and default absent-web-root location now checked.
- Medium, FIXED: a failure during audit append after promotion could leave an object without attempting compensation; early validation also did not consistently own/dispose the provided stream. Expanded compensation and explicit stream ownership.
- Low, FIXED: existing joined StoredFile rollback assertions could not detect orphan rows; one purported one-byte source did not override the Memory<byte> read overload used by production. New independent SQL failure/direct-row tests and genuine one-byte stream tests remove those evidence gaps.
- Informational: framework multipart files may spool to private temporary disk; download success is disclosure approval rather than delivery confirmation; offset pages can shift; hard crash/failed object compensation can leave orphan bytes.
- Informational: ClinicDb has SIX pending migrations, not only the two file-storage migrations. Details below. No schema change was applied.

## 3. UPLOAD ATOMICITY VERDICT

PASS. AttachmentService adds StoredFile and PatientAttachment, appends file.upload using the same scoped ClinicDbContext, then calls AttachmentStore.SaveAsync once. No earlier SaveChanges in the upload workflow. The audit mutation scope does not save independently.

EachSqlInsertFailureRollsBackAllRowsAndDeletesPromotedBytes independently injects a SQL THROW at StoredFiles, PatientAttachments, and AuditEvents INSERT, using MaxBatchSize(1) to exercise separate commands within EF's transaction. The save observer asserts all three entities are staged at the one save. Fresh contexts directly query captured file ID/actor, attachment ID/patient, and file.upload. All absent. The promoted key no longer exists and the private root has no remaining files. The success test verifies object bytes, file row, attachment row, and exactly one event.

## 4. STORED FILE ORPHAN ROW VERDICT

PASS. Upload metadata and clinical link have no separate save path. Independent insert failures cannot leave committed StoredFile rows without links. Generic Phase 1 storage intentionally permits standalone metadata; the attachment upload does not call that independent-save service. A process crash or failed compensation can orphan bytes, not a separately committed upload metadata row.

## 5. CROSS-PATIENT SECURITY VERDICT

PASS. Known foreign-patient visits are rejected with hidden 404 before any request-body read (measured). Store queries bind patient + visit/attachment IDs. Foreign attachment download returns a generic 404 without filename, MIME, size, internal IDs/key/hash, disposition, or successful download audit. Listing the requester's own patient returns only that patient's rows; an authorized empty list records its own file.list, never a foreign-resource disclosure event.

## 6. DOCTOR AUTHORITY VERDICT

PASS. Persisted StaffUser.AssociatedDoctorId must equal Visit.DoctorId. Correct association succeeds; null/wrong association denies. A changed association is read on the existing cookie's next request. Appointment/schedule grants, an appointment linked to a different doctor, and form/query doctor values do not replace visit authority. DoctorAssistant remains patient scope + attachments.write + MFA, without an association requirement.

## 7. FILE TYPE / SIGNATURE VERDICT

PASS. PDF, JPEG and PNG signatures tested; empty, one-byte and every truncated-prefix length rejected; valid one-byte reads accepted; mismatched PDF/JPEG and fake PNG rejected. Uppercase extension/MIME accepted. Content-Type parameters intentionally rejected by the exact MIME allowlist. No whole-file buffering in application validation.

## 8. STREAM CORRECTNESS VERDICT

PASS after fix. Small destination buffers drain the entire prefix without loss/duplication. Genuine non-seekable, one-byte source -> validator -> local staging -> promotion -> readback preserves all bytes, SHA-256 and SizeBytes. Cancellation/read errors propagate. Input ownership/disposal covers early rejection and failures; no seek/length dependency. Existing storage cancellation/failure tests cover staging cleanup.

## 9. HTTP REQUEST SIZE VERDICT

PASS. Both upload endpoints alone use AttachmentUploadGateAttribute. Total request cap = Attachments:MaxFileSizeBytes + 65,536 (default 10,551,296 bytes). Sets IHttpMaxRequestBodySizeFeature when writable and independently limits actual bytes through a non-buffering request stream, even without Content-Length. At most one extra byte is read to detect overflow. MultipartBodyLengthLimit uses the same cap; field size 65,536, value count 16, multipart headers 16,384. Application file limit remains 10,485,760 by default; storage has its independent backstop. Unknown-length-body test proves bounded reads, rejection, zero rows and no stored objects. MVC form-binding rejection is 400; server rejection may be 413; application file-size rejection is 413/file_too_large. No global limit changed.

## 10. MULTIPART/TEMP STORAGE VERDICT

PASS, with framework behavior explicitly documented. IFormFile model binding buffers files; above its default 64 KiB memory threshold it can spool to ASPNETCORE_TEMP or the process user's temp directory. In this environment ASPNETCORE_TEMP is unset and TEMP is C:\Users\user\AppData\Local\Temp. No project whole-file memory copy or static/public write exists in the upload path. Provider roots equal to/below wwwroot are rejected; framework temp must remain private in deployment. Framework owns request buffers; application owns opened upload streams and disposes them on cancellation/failure.

Source: [Microsoft ASP.NET Core upload documentation](https://learn.microsoft.com/en-us/aspnet/core/mvc/models/file-uploads?view=aspnetcore-10.0).

## 11. CSRF ORDER VERDICT

PASS. Header-only CSRF runs before model binding, IFormFile opening, StageAsync, inserts and audit append. Missing/invalid tokens tested with a request-body meter: zero bytes read; fresh direct StoredFile query, attachment and audit queries empty; no stored objects.

## 12. CONTENT-DISPOSITION VERDICT

PASS. Arabic, spaces, double/single quotes, semicolon, percent, emoji, CR, LF, backslash, slash, and very long names tested. Parseable attachment-only disposition with ASCII filename fallback and RFC 5987 filename*, no extra injected parameters, CR/LF, or key leakage. Never inline. Upload domain validation additionally rejects controls and names over 255 characters; disposition sanitization remains defense in depth.

## 13. DOWNLOAD FAIL-CLOSED VERDICT

PASS. Verified authorize -> metadata -> successful open -> durable file.download -> response construction. On audit failure, the opened stream is disposed, zero body bytes emitted and no Content-Disposition set; HTTP test returns sanitized error. Missing backing object emits no successful download audit.

## 14. DOWNLOAD SUCCESS SEMANTICS

Succeeded means the server authorized/resolved and opened the object and approved disclosure after durable audit persistence. It does not prove the client received every byte. Disconnect/cancellation during streaming can interrupt delivery after the event. No schema change.

## 15. LIST PAGINATION VERDICT

PASS. Deterministic CreatedAtUtc DESC, Id DESC; page 1..1000, pageSize 1..100 (default 20), bounded Take; invalid ranges rejected without audit; patient filter cannot be escaped. Offset shifting after concurrent uploads is acceptable for attachment browsing absent historical traversal requirements. No redesign.

## 16. PERMISSION REVALIDATION VERDICT

PASS. Explicit tests separately remove attachments.read, attachments.write and patient_record_id from persisted claims after cookie authentication. Existing cookies immediately receive 401 on attachment access; scope removal tested for read and upload.

## 17. DTO / PRIVACY VERDICT

PASS. Upload/list projections expose only intended attachment/display metadata. No StorageKey, Sha256, StoredFileId, CreatedByStaffId, path, provider or EF navigation objects. Download headers use only sanitized display name/content type and protection headers.

## 18. MIGRATION VERDICT

PASS. 20260916224607_AddPatientAttachments has three Restrict FKs (Patient, Visit, StoredFile) and UNIQUE StoredFileId. No cascades. Phase 1 migration unchanged during this gate. Model drift clean. Both file-storage migrations remain pending.

## 19. FIXES MADE — exact files and reasons

Production:
- backend/Clinic/src/Clinic.Application/Attachments/AttachmentFilePolicy.cs — preserve partial prefix reads; Memory<byte> override, cancellation, empty-file result, explicit leave-open/disposal semantics.
- backend/Clinic/src/Clinic.Application/Attachments/AttachmentService.cs — own/dispose input even before validation; compensate promoted object and discard pending rows for append/add/save failures.
- backend/Clinic/src/Clinic.Api/Controllers/AttachmentUploadGateAttribute.cs — NEW endpoint-only configured request/multipart limits, actual-byte wrapper, pre-binding CSRF and patient/visit/doctor authorization.
- backend/Clinic/src/Clinic.Api/Controllers/StaffAttachmentsController.cs — apply upload gate instead of fixed RequestSizeLimit attributes.
- backend/Clinic/src/Clinic.Application/Storage/FileStorageOptions.cs — public-root equality/descendant validation helper.
- backend/Clinic/src/Clinic.Api/Program.cs — apply that helper with default wwwroot fallback.

Tests:
- backend/Clinic/tests/xUnit Test Project/AttachmentTests.cs — append-failure compensation regression and idempotent discard assertion.
- backend/Clinic/tests/xUnit Test Project/AttachmentStreamGateTests.cs — NEW short-prefix, real one-byte/non-seekable, full storage byte/hash/size, MIME, cancellation/read-error tests.
- backend/Clinic/tests/xUnit Test Project/AttachmentDownloadGateTests.cs — NEW ordering, audit-failure disposal, no pre-audit response, missing-object tests.
- backend/Clinic/tests/xUnit Test Project/FileStoragePublicRootGateTests.cs — NEW equality/descendant/normalized-path/private-sibling root tests.
- backend/Clinic/tests/Clinic.IntegrationTests/Api/StaffAttachmentHttpTests.cs — partial test class, extra persisted claims helper, stronger DTO/hidden-resource/header assertions.
- backend/Clinic/tests/Clinic.IntegrationTests/Api/StaffAttachmentHardeningTests.cs — NEW independent SQL failures, direct orphan checks, explicit grant revocation, disposition matrix, pagination bounds.
- backend/Clinic/tests/Clinic.IntegrationTests/Api/StaffAttachmentRequestGateTests.cs — NEW pre-binding body-meter, unknown-length request limit, persisted doctor/appointment/schedule/client-authority and assistant coverage.

Documentation:
- docs/file-storage-phase-2.md — gate behavior, limits, framework spool behavior, disclosure semantics and evidence.
- docs/file-storage-final-gate.md — this final report and Git status.

## 20. TEST RESULTS

| Suite | Passed | Failed | Skipped |
|---|---:|---:|---:|
| Unit | 245 | 0 | 0 |
| Integration | 414 | 0 | 0 |
| Total | 659 | 0 | 0 |

Full requested command: dotnet test backend/Clinic/Clinic.slnx --no-build --no-restore --nologo -m:1 --verbosity minimal.
Initial sandbox SQL tests failed before execution due to Windows SQL encryption support; the full final suite passed outside the sandbox using existing disposable ClinicTests_* databases. These fixtures create/migrate/delete only their isolated test databases, not ClinicDb.

## 21. BUILD RESULT

PASS. dotnet build backend/Clinic/Clinic.slnx --nologo -m:1 -p:UseSharedCompilation=false: zero warnings, zero errors.

## 22. EF DRIFT

PASS. dotnet ef migrations has-pending-model-changes --no-build --project backend/Clinic/src/Clinic.Infrastructure --startup-project backend/Clinic/src/Clinic.Api reports no changes since the last migration.

## 23. CLINICDB STATUS

Untouched by this gate; migration list was read-only. No database update or application migration execution against ClinicDb.

Actual pending migrations from the requested migrations list command:
- 20260915105237_AddPatientMedicalRecordFoundation
- 20260915192837_AddVisitsAndVitalMeasurements
- 20260915201721_AddMedicationCatalogAndPrescriptions
- 20260916161906_AddAuditFoundation
- 20260916214417_AddFileStorageFoundation
- 20260916224607_AddPatientAttachments

Latest applied migration listed: 20260914205919_AddStaffIdentity. This contradicts the starting assumption that only two migrations were pending. Reconcile deployment/database baseline during later rollout planning; do not apply automatically.

## 24. STRANDED FILE STATUS

Both retained unchanged and untracked:
- backend/Clinic/src/Clinic.Infrastructure/Persistence/Migrations/20260916110026_AddAuditFoundation.cs.stranded
- backend/Clinic/src/Clinic.Infrastructure/Persistence/Migrations/20260916110026_AddAuditFoundation.Designer.cs.stranded

MSBuild evaluated Compile items contain zero .stranded files. Neither appears as an EF migration. They are operator cleanup items only: not renamed, compiled, staged, or deleted. OneDrive cannot currently hydrate their contents; content inspection was unnecessary to confirm exclusion.

## 25. PRE-EXISTING AUDIT DOC STATUS

Retained untouched, unstaged and untracked; excluded from the proposed file-storage commit:
- docs/audit-trail-phase-3.md
- docs/progress-2026-09-16.md

## 26. GIT STATUS

HEAD remains f548588. Index empty. Phases 1+2 remain uncommitted. git diff --check passes (Git reports only line-ending normalization notices).

Full status recorded below.

## 27. COMMIT PLAN RECOMMENDATION

Safest: one explicitly scoped commit for the complete, jointly verified File Storage Phases 1+2 foundation + attachments + hardening, including the two new migrations, final snapshot, wiring/configuration, tests and file-storage documentation. Shared wiring/snapshot currently spans both phases; splitting now adds avoidable intermediate-state risk. Review and explicitly stage only intended files; avoid git add . and exclude both old audit docs and both .stranded files. Review the staged diff and file list before committing. If separate historical phase commits are mandatory, reconstruct/review intermediate wiring and snapshots in a separate workspace and verify each before committing. No commit/staging performed here.

## 28. NEXT STEP

FILE STORAGE PHASE 3 — FEATURE ROLLOUT PLANNING ONLY.

No Phase 3 implementation. STOP.

## Final git status --short

```text
 M .gitignore
 M backend/Clinic/src/Clinic.Api/Program.cs
 M backend/Clinic/src/Clinic.Api/appsettings.Development.json
 M backend/Clinic/src/Clinic.Api/appsettings.json
 M backend/Clinic/src/Clinic.Infrastructure/Authentication/StaffAuthentication.cs
 M backend/Clinic/src/Clinic.Infrastructure/Persistence/ClinicDbContext.cs
 M backend/Clinic/src/Clinic.Infrastructure/Persistence/Migrations/ClinicDbContextModelSnapshot.cs
 M backend/Clinic/tests/Clinic.IntegrationTests/Api/BookingApiFactory.cs
 M backend/Clinic/tests/Clinic.IntegrationTests/Api/ErrorHandlingApiFactory.cs
?? backend/Clinic/src/Clinic.Api/Controllers/AttachmentUploadGateAttribute.cs
?? backend/Clinic/src/Clinic.Api/Controllers/StaffAttachmentsController.cs
?? backend/Clinic/src/Clinic.Application/Attachments/
?? backend/Clinic/src/Clinic.Application/Storage/
?? backend/Clinic/src/Clinic.Domain/Entities/PatientAttachment.cs
?? backend/Clinic/src/Clinic.Domain/Entities/StoredFile.cs
?? backend/Clinic/src/Clinic.Infrastructure/Persistence/AttachmentMapping.cs
?? backend/Clinic/src/Clinic.Infrastructure/Persistence/FileStorageMapping.cs
?? backend/Clinic/src/Clinic.Infrastructure/Persistence/Migrations/20260916110026_AddAuditFoundation.Designer.cs.stranded
?? backend/Clinic/src/Clinic.Infrastructure/Persistence/Migrations/20260916110026_AddAuditFoundation.cs.stranded
?? backend/Clinic/src/Clinic.Infrastructure/Persistence/Migrations/20260916214417_AddFileStorageFoundation.Designer.cs
?? backend/Clinic/src/Clinic.Infrastructure/Persistence/Migrations/20260916214417_AddFileStorageFoundation.cs
?? backend/Clinic/src/Clinic.Infrastructure/Persistence/Migrations/20260916224607_AddPatientAttachments.Designer.cs
?? backend/Clinic/src/Clinic.Infrastructure/Persistence/Migrations/20260916224607_AddPatientAttachments.cs
?? backend/Clinic/src/Clinic.Infrastructure/Repositories/AttachmentStore.cs
?? backend/Clinic/src/Clinic.Infrastructure/Storage/
?? backend/Clinic/tests/Clinic.IntegrationTests/Api/StaffAttachmentHardeningTests.cs
?? backend/Clinic/tests/Clinic.IntegrationTests/Api/StaffAttachmentHttpTests.cs
?? backend/Clinic/tests/Clinic.IntegrationTests/Api/StaffAttachmentRequestGateTests.cs
?? backend/Clinic/tests/Clinic.IntegrationTests/Infrastructure/StoredFilePersistenceTests.cs
?? "backend/Clinic/tests/xUnit Test Project/AttachmentDownloadGateTests.cs"
?? "backend/Clinic/tests/xUnit Test Project/AttachmentStreamGateTests.cs"
?? "backend/Clinic/tests/xUnit Test Project/AttachmentTests.cs"
?? "backend/Clinic/tests/xUnit Test Project/FileStoragePublicRootGateTests.cs"
?? "backend/Clinic/tests/xUnit Test Project/FileStorageTests.cs"
?? "backend/Clinic/tests/xUnit Test Project/StoredFileDomainTests.cs"
?? docs/audit-trail-phase-3.md
?? docs/file-storage-final-gate.md
?? docs/file-storage-phase-1.md
?? docs/file-storage-phase-2.md
?? docs/progress-2026-09-16.md
?? docs/progress-2026-09-17.md
```
