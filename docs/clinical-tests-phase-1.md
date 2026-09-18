# Clinical tests — Phase 1: request/order foundation

## Scope and model

One create-once `ClinicalTestRequest` aggregate supports `Lab` and `Imaging`. Test names
are entered directly; no test catalog, medical seed data, reference ranges, units, LOINC,
DICOM/PACS or external laboratory integration is introduced.

Fields: Id, PatientId, nullable VisitId, RequestedByDoctorId, Category, TestName,
ClinicalInstructions, Status, RequestedAtUtc, nullable UploadedAtUtc, nullable ReviewedAtUtc,
nullable ReviewedByDoctorId and SQL Server RowVersion. IDs and creation time are server-owned.
TestName is required, at most 200 characters; optional ClinicalInstructions is at most 2000.
Arabic/English Unicode is supported. Control characters (including tabs/newlines), excessive
raw lengths, blank names, empty identifiers and undefined enum values are rejected. Text is
trimmed; blank optional instructions become null.

## Lifecycle and history

The reserved enum is Requested -> Uploaded -> UnderReview -> Reviewed. Phase 1 exposes ONLY
NEW -> Requested. There are no public setters or domain mutation/delete methods, no editing,
no PATCH/PUT/status or DELETE routes. Result/review timestamps and reviewer remain null.
The new table has a Phase 1 lifecycle check constraint enforcing exactly that shape, plus a
category check. Phase 2 must deliberately extend the lifecycle constraint in a new migration
when it introduces actual result attachment transitions; it must not edit this historical migration.

The SQL rowversion is generated and configured as a concurrency token for later transitions.
It is not returned by this phase because there is no write-after-create endpoint requiring it.
No new token encoding convention is introduced.

## Patient and doctor authority

Patient is mandatory. Visit is optional; supplied visits must resolve under the same patient.
The service performs a patient-bound visit lookup before tracking any new order. Known foreign
visits and nonexistent visits share a hidden 404. Appointments are never clinical authority.

The API supplies the actor exclusively from authenticated staff claims. The service queries
persisted StaffUser.AssociatedDoctorId with AsNoTracking on each create. For visit-linked orders
it must equal Visit.DoctorId. For standalone orders a non-null associated doctor becomes
RequestedByDoctorId. Standalone ordering is therefore permitted only to an authenticated Doctor
with MFA, explicit tests.write and the exact patient scope, plus a persisted doctor association.
No client-supplied doctor, status or timestamp participates in creation. Extra JSON fields are
ignored under the existing serializer convention and cannot override server facts.
Appointment/schedule scopes do not substitute for this authority.

## Permissions and roles

New explicit persisted permission names: tests.read, tests.write. They are declared in the
permission catalog only; no accounts receive grants automatically. Existing cookie revalidation
checks persisted permission/scope claims on every request. Removing either grant or a patient
scope immediately invalidates an already-authenticated cookie (401).

| Operation | Role | Additional requirements |
|---|---|---|
| List/detail | Doctor or DoctorAssistant | MFA + tests.read + exact patient_record_id |
| Create | Doctor only | MFA + tests.write + exact patient_record_id + doctor authority + CSRF |

Receptionist has no access. DoctorAssistant cannot create even with tests.write, cannot select
the ordering doctor, and has no review operation. Existing attachment/vitals assistant behavior
is unchanged.

## API

All routes are under `/api/staff/patients/{patientId}/test-requests` and use the existing protected
staff session. Clinical responses use no-store. Controller request-size ceiling is 32 KiB.

POST body:

```json
{
  "category": "Lab",
  "testName": "CBC",
  "visitId": null,
  "clinicalInstructions": "Optional clinical instructions"
}
```

POST requires X-CSRF-TOKEN, matching the existing cookie-authenticated mutation convention.
It returns 201 with Location pointing to details, and id, patientId, visitId, category, testName,
status, requestedAtUtc. Category/status JSON values are enum names; numeric body enums and unknown
names are rejected. Converters are registered specifically for these enums in MVC and HTTP JSON,
leaving existing API serialization unchanged.

GET collection accepts optional category/status filters and page/pageSize. Defaults: page 1,
pageSize 20. Bounds: page 1..1000, pageSize 1..100, matching attachment-list conventions and
preventing offset overflow. Response: `{ items, page, pageSize }`. Items use the create summary
shape; instructions are omitted. Ordering is RequestedAtUtc DESC then Id DESC. Offset pages can
shift as requests are created; no historical snapshot guarantee is claimed. Reserved status
filters are accepted and return no rows until later phases support those states.

GET `/{requestId}` returns the same fields plus clinicalInstructions. Queries bind both patient
and request ID; a known foreign request returns hidden 404 and no successful read event.
Neither response exposes RowVersion, staff identity data, requester/reviewer internal IDs,
patient name, EF navigation properties or storage data.

Errors follow existing ProblemDetails: 400 invalid_input / invalid_csrf_token (model-binding
errors use the existing automatic validation format), 403 for role/permission/scope/doctor
authority, 404 patient_not_found or test_request_not_found for hidden/missing resources, and
sanitized 500 unexpected_error for persistence failure. No artificial 409 is introduced because
this phase has no conflicting update transition.

## Audit guarantees

| Operation | ActionCode | ResourceType | ResourceId | PatientId | Metadata |
|---|---|---|---|---|---|
| Create | test-request.create | test-request | request ID, N format | route patient | empty |
| List | test-request.list | patient | patient ID, N format | route patient | empty |
| Detail | test-request.read | test-request | request ID, N format | route patient | empty |

All successful events use the authenticated actor and Succeeded. Create uses the existing
IAuditMutationWriter scope: add order, append event, ONE shared ClinicDbContext SaveChanges.
Audit INSERT failure rolls back the order; rejected pending order/event state is detached by
its respective owner. Validation/authorization/hidden failures emit no Succeeded event.

Reads use HttpAccessAudit after authorization and successful resource resolution but before
returning the clinical DTO. Standalone access audit persistence must succeed first; failure
returns sanitized error with no clinical payload. Repeated successful reads legitimately create
one event each. No test name, instructions, patient name, diagnosis or notes enter audit metadata;
this phase elects to use no optional category metadata at all.

## Persistence and migration

Exactly one new migration: `20260917034049_AddClinicalTestRequests`.
It adds only ClinicalTestRequests, checks, indexes and four FKs FROM the new table:
PatientId -> Patients; VisitId -> Visits (nullable); RequestedByDoctorId -> Doctors;
ReviewedByDoctorId -> Doctors (nullable). All Restrict (SQL NO ACTION), with no cascading clinical
deletes. Existing tables and historical migrations are unchanged.

Indexes: PatientId + RequestedAtUtc, VisitId + RequestedAtUtc, plus FK support indexes on
RequestedByDoctorId and ReviewedByDoctorId. Global Status/Category queue indexes are deferred:
current reads are patient-scoped and no global queue exists.

There is no PatientAttachmentId, StoredFile reference or result attachment relationship yet.
Visit/patient matching is enforced by the service, following existing clinical aggregate patterns.
This migration is generated only, not applied to ClinicDb. SQL tests use isolated ClinicTests_*
databases and migrate/delete only those disposable fixtures.

## Verification coverage

ClinicalTestDomainTests covers both categories, Requested-only state, UTC time, required/optional
IDs, Unicode, exact bounds, controls, invalid enums and absence of public mutation/delete methods.
ClinicalTestPersistenceTests covers round-trip, nullable visit, rowversion, all four restrictive
FKs, real delete rejection, deterministic tie ordering, page/category/status filters, patient
isolation, service mismatch rejection and SQL rejection of premature result states.
StaffClinicalTestsHttpTests covers server authority/time, spoofed server-owned fields, roles,
MFA, scope, permissions, live doctor changes, appointment/schedule grants, standalone authority,
known foreign visits/requests, CSRF, validation, immediate grant removal, transactional audit
failure, fail-closed list/details, audit shape/privacy, pagination and absent mutation routes.

## Deferred work and next phase

No result-file upload, PatientAttachment link, Uploaded/UnderReview/Reviewed transition, catalog,
patient uploads, notifications, background jobs, external laboratories, HL7/FHIR, OCR, archive,
Doctor Notebook, Flutter or staff UI exists in this slice.

Next: CLINICAL TESTS PHASE 2 — RESULT ATTACHMENT UPLOAD + CONTROLLED STATUS TRANSITIONS.
Future Phase 3: review/finalization design and implementation. No later-phase work started.

## Final implementation report — 2026-09-17

1. **EXECUTIVE RESULT:** READY FOR CLINICAL TESTS PHASE 2.
2. **FILES CHANGED:** exact phase-specific list below. Pre-existing file-storage work retained.
3. **DOMAIN MODEL:** one ClinicalTestRequest, required patient/requester doctor, optional visit,
   bounded clinical text, category/status/timestamps and RowVersion.
4. **STATUS LIFECYCLE:** Requested/Uploaded/UnderReview/Reviewed reserved; only NEW -> Requested
   implemented. SQL constraint rejects premature result/review state.
5. **LAB / IMAGING DESIGN:** one category enum and aggregate, no duplicated request models/catalog.
6. **DATABASE RELATIONSHIPS:** four Restrict/SQL NO ACTION FKs; nullable visit/reviewer; no attachment FK.
7. **PERMISSIONS:** tests.read/tests.write declared, no automatic grants; live persisted revalidation.
8. **ROLE POLICY:** Doctor creates; Doctor/DoctorAssistant read; MFA + exact patient scope + permission.
   Receptionist denied; assistant cannot create even with tests.write.
9. **DOCTOR AUTHORITY:** live persisted AssociatedDoctorId, matching Visit.DoctorId when linked;
   standalone requires a persisted association. Client/appointment/schedule doctor signals ignored.
10. **CREATE API:** POST /api/staff/patients/{patientId}/test-requests, CSRF required, 201 + Location,
    server IDs/doctor/time/status, no internal staff/concurrency fields.
11. **LIST API:** GET collection, optional category/status, page 1..1000, size 1..100/default 20,
    RequestedAtUtc DESC + Id DESC, summary projection without instructions.
12. **DETAIL API:** GET /{requestId}, patient-bound lookup, authorized clinical instructions included.
13. **PATIENT-SCOPE ISOLATION:** cross-patient visit/request hidden 404; wrong patient scope 403;
    history query cannot escape its patient predicate.
14. **MUTATION AUDIT:** exactly one test-request.create coupled to order in one SaveChanges;
    fresh-context audit failure test confirms neither row survives.
15. **READ FAIL-CLOSED AUDIT:** exactly one test-request.list/read per successful access;
    audit failure discloses no protected DTO and returns sanitized 500.
16. **AUDIT METADATA SAFETY:** empty metadata for all three events; no clinical names/instructions.
17. **CONCURRENCY / ROWVERSION:** SQL-generated 8-byte rowversion, concurrency token configured;
    no update endpoint and no token disclosure/encoding yet.
18. **VALIDATION / ERROR SEMANTICS:** GUID/enum/text/bounds checks; 400/403/404 and sanitized 500;
    no artificial 409; existing cookie revocation yields 401.
19. **MIGRATION:** 20260917034049_AddClinicalTestRequests; exactly one additive migration,
    adds only the new table with its indexes/checks/FKs. NOT applied to ClinicDb.
20. **UNIT TEST RESULTS:** 265 passed; 20 new clinical-test domain cases; 0 failed/skipped.
21. **INTEGRATION TEST RESULTS:** 469 passed; 55 new cases (45 HTTP, 10 SQL); 0 failed/skipped.
22. **FULL TEST RESULT:** 734 passed, 0 failed, 0 skipped (75 new above the 659-test baseline).
23. **BUILD RESULT:** 0 warnings, 0 errors with the requested single-process/no-shared-compiler command.
24. **EF DRIFT:** no changes since last migration. Migration list confirms new migration Pending.
25. **CLINICDB STATUS:** untouched. Only disposable ClinicTests_* fixtures were migrated for tests.
    Read-only migration listing confirms seven pending migrations: patient records, visits/vitals,
    medication/prescriptions, audit, file storage, attachments, and the new clinical test requests.
26. **DOCUMENTATION:** this document covers design/security/APIs/lifecycle/limits/deferred work;
    docs/progress-2026-09-17.md updated with this phase and verification.
27. **GIT STATUS:** baseline HEAD f548588; existing changes retained; no staging/commit/push or
    reset/restore/amend. Final status below. Both old audit docs and both stranded migration
    leftovers retained unchanged. git diff --check passed; only line-ending notices were emitted.
28. **DEFERRED WORK:** result attachment upload, PatientAttachment link, Uploaded/UnderReview/Reviewed
    transitions, lab/imaging catalog, patient uploads, notifications, external integration,
    OCR, jobs, archive/notebook and all UI work.
29. **NEXT STEP:** CLINICAL TESTS PHASE 2 — RESULT ATTACHMENT UPLOAD + CONTROLLED STATUS TRANSITIONS.
    Phase 2 was NOT implemented. STOP.

### Exact phase-specific files

New source:
- backend/Clinic/src/Clinic.Domain/Entities/ClinicalTestRequest.cs
- backend/Clinic/src/Clinic.Domain/Enums/ClinicalTestEnums.cs
- backend/Clinic/src/Clinic.Application/ClinicalTests/ClinicalTestModels.cs
- backend/Clinic/src/Clinic.Application/ClinicalTests/ClinicalTestService.cs
- backend/Clinic/src/Clinic.Infrastructure/Persistence/ClinicalTestMapping.cs
- backend/Clinic/src/Clinic.Infrastructure/Repositories/ClinicalTestStore.cs
- backend/Clinic/src/Clinic.Api/Controllers/StaffClinicalTestsController.cs

Existing wiring updated:
- backend/Clinic/src/Clinic.Api/Program.cs — DI, two policies, type-specific JSON enum converters.
- backend/Clinic/src/Clinic.Infrastructure/Authentication/StaffAuthentication.cs — permission catalog.
- backend/Clinic/src/Clinic.Infrastructure/Persistence/ClinicDbContext.cs — DbSet and mapping.
- backend/Clinic/src/Clinic.Infrastructure/Persistence/Migrations/ClinicDbContextModelSnapshot.cs — new model only.

Generated migration:
- backend/Clinic/src/Clinic.Infrastructure/Persistence/Migrations/20260917034049_AddClinicalTestRequests.cs
- backend/Clinic/src/Clinic.Infrastructure/Persistence/Migrations/20260917034049_AddClinicalTestRequests.Designer.cs

New tests:
- backend/Clinic/tests/xUnit Test Project/ClinicalTestDomainTests.cs
- backend/Clinic/tests/Clinic.IntegrationTests/ClinicalTests/ClinicalTestPersistenceTests.cs
- backend/Clinic/tests/Clinic.IntegrationTests/Api/StaffClinicalTestsHttpTests.cs

Documentation:
- docs/clinical-tests-phase-1.md
- docs/progress-2026-09-17.md

### Verification commands

```text
git diff --check
dotnet build backend/Clinic/Clinic.slnx --nologo -m:1 -p:UseSharedCompilation=false
dotnet test backend/Clinic/Clinic.slnx --no-build --no-restore --nologo -m:1 --verbosity minimal
dotnet ef migrations has-pending-model-changes --no-build --project backend/Clinic/src/Clinic.Infrastructure --startup-project backend/Clinic/src/Clinic.Api
dotnet ef migrations list --project backend/Clinic/src/Clinic.Infrastructure --startup-project backend/Clinic/src/Clinic.Api
git status --short
```

The first targeted HTTP run exposed a test-clock/ticket-expiry mismatch; tickets now use the
same fixed TimeProvider instant as the test host. Production authentication was not changed.
After that correction all 75 feature cases and the complete 734-test suite passed. SQL tests
ran outside the restricted sandbox to support Windows SQL encryption, using isolated fixtures.
Migration scaffolding was completed with --no-build after a clean explicit build because the
initial default design-time build failed; exactly one migration was generated.

### Final status

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
?? backend/Clinic/src/Clinic.Api/Controllers/StaffClinicalTestsController.cs
?? backend/Clinic/src/Clinic.Application/Attachments/
?? backend/Clinic/src/Clinic.Application/ClinicalTests/
?? backend/Clinic/src/Clinic.Application/Storage/
?? backend/Clinic/src/Clinic.Domain/Entities/ClinicalTestRequest.cs
?? backend/Clinic/src/Clinic.Domain/Entities/PatientAttachment.cs
?? backend/Clinic/src/Clinic.Domain/Entities/StoredFile.cs
?? backend/Clinic/src/Clinic.Domain/Enums/ClinicalTestEnums.cs
?? backend/Clinic/src/Clinic.Infrastructure/Persistence/AttachmentMapping.cs
?? backend/Clinic/src/Clinic.Infrastructure/Persistence/ClinicalTestMapping.cs
?? backend/Clinic/src/Clinic.Infrastructure/Persistence/FileStorageMapping.cs
?? backend/Clinic/src/Clinic.Infrastructure/Persistence/Migrations/20260916110026_AddAuditFoundation.Designer.cs.stranded
?? backend/Clinic/src/Clinic.Infrastructure/Persistence/Migrations/20260916110026_AddAuditFoundation.cs.stranded
?? backend/Clinic/src/Clinic.Infrastructure/Persistence/Migrations/20260916214417_AddFileStorageFoundation.Designer.cs
?? backend/Clinic/src/Clinic.Infrastructure/Persistence/Migrations/20260916214417_AddFileStorageFoundation.cs
?? backend/Clinic/src/Clinic.Infrastructure/Persistence/Migrations/20260916224607_AddPatientAttachments.Designer.cs
?? backend/Clinic/src/Clinic.Infrastructure/Persistence/Migrations/20260916224607_AddPatientAttachments.cs
?? backend/Clinic/src/Clinic.Infrastructure/Persistence/Migrations/20260917034049_AddClinicalTestRequests.Designer.cs
?? backend/Clinic/src/Clinic.Infrastructure/Persistence/Migrations/20260917034049_AddClinicalTestRequests.cs
?? backend/Clinic/src/Clinic.Infrastructure/Repositories/AttachmentStore.cs
?? backend/Clinic/src/Clinic.Infrastructure/Repositories/ClinicalTestStore.cs
?? backend/Clinic/src/Clinic.Infrastructure/Storage/
?? backend/Clinic/tests/Clinic.IntegrationTests/Api/StaffAttachmentHardeningTests.cs
?? backend/Clinic/tests/Clinic.IntegrationTests/Api/StaffAttachmentHttpTests.cs
?? backend/Clinic/tests/Clinic.IntegrationTests/Api/StaffAttachmentRequestGateTests.cs
?? backend/Clinic/tests/Clinic.IntegrationTests/Api/StaffClinicalTestsHttpTests.cs
?? backend/Clinic/tests/Clinic.IntegrationTests/ClinicalTests/
?? backend/Clinic/tests/Clinic.IntegrationTests/Infrastructure/StoredFilePersistenceTests.cs
?? "backend/Clinic/tests/xUnit Test Project/AttachmentDownloadGateTests.cs"
?? "backend/Clinic/tests/xUnit Test Project/AttachmentStreamGateTests.cs"
?? "backend/Clinic/tests/xUnit Test Project/AttachmentTests.cs"
?? "backend/Clinic/tests/xUnit Test Project/ClinicalTestDomainTests.cs"
?? "backend/Clinic/tests/xUnit Test Project/FileStoragePublicRootGateTests.cs"
?? "backend/Clinic/tests/xUnit Test Project/FileStorageTests.cs"
?? "backend/Clinic/tests/xUnit Test Project/StoredFileDomainTests.cs"
?? docs/audit-trail-phase-3.md
?? docs/clinical-tests-phase-1.md
?? docs/file-storage-final-gate.md
?? docs/file-storage-phase-1.md
?? docs/file-storage-phase-2.md
?? docs/progress-2026-09-16.md
?? docs/progress-2026-09-17.md
```
