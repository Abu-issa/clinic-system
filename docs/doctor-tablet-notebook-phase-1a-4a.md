# Doctor Tablet Phase 1A-4A: notebook metadata foundation

## Contract

All routes start with `/api/staff/patients/{patientId}/notebook/pages`.

- `POST`: JSON `{ "title": "Consultation notes", "visitId": null, "clientDraftId": null, "originDeviceId": null }`. Title is required, trimmed, maximum 200 characters. Optional draft/device identifiers are trimmed strings of at most 128 characters. Returns 201 and a Location header.
- `GET`: optional `page` (1–100, default 1), `pageSize` (1–20, default 10). Returns `{ items, page, pageSize, hasMore }`, ordered by creation time and ID descending. This is bounded offset pagination, not a stable snapshot during concurrent inserts.
- `GET /{pageId}`: returns one page; lookup includes both route patient ID and page ID.

Create/detail response fields: `id`, `patientId`, `visitId`, `authorDoctorId`, `title`, `createdAtUtc`, `updatedAtUtc`, `finalizedAtUtc`, `finalizedByDoctorId`, `currentRevisionNumber`, `rowVersion` (base64 SQL rowversion). List items contain identifiers, title, author, creation/update time and current revision number. Finalization fields remain null.

Create atomically writes a page, revision 1 (`Kind = Created`), and a `notebook.page.create` audit event. Revision 1 describes creation only; it contains no clinical ink or file payload. Author staff and doctor identity are derived from the authenticated account; timestamps come from the server TimeProvider. Unknown request fields cannot override these values. A supplied visit must belong to the route patient.

Draft uniqueness is **per page**, enforced by a filtered unique `(PageId, ClientDraftId)` index. It is a foundation for future revision idempotency, not idempotency of page creation: retrying a POST can create another page. Device identifiers are untrusted metadata, not device binding.

## Authentication and authorization

The endpoint-specific `ClinicNotebookStaff` selector uses the existing mobile bearer handler whenever an Authorization header is present; otherwise it uses the existing ClinicStaff cookie. Invalid bearer credentials cannot fall back to a cookie. The application's default cookie authentication is unchanged. Cookie POSTs require existing antiforgery validation; bearer POSTs do not require cookie CSRF tokens.

Existing StaffSession MFA/role validation, Identity account and session validation, persisted permission claims, and exact `patient_record_id` resource policies are reused. Notebook claims/scopes are fetched from Identity for each operation, including mobile sessions whose tokens intentionally contain no patient permissions.

- Doctor: read requires `notebook.read`; create requires `notebook.write` and a valid associated doctor record.
- DoctorAssistant: read requires `notebook.read`; create is denied even if write permission is mistakenly granted.
- Receptionist: denied by notebook role policies even if notebook claims are granted.
- Every operation also requires an exact patient scope. Wildcard scopes do not grant access. Grants must be assigned through existing staff administration; existing users receive no automatic clinical grants.

Successful list/detail reads use fail-closed `HttpAccessAudit.RecordAsync` with `notebook.page.list` / `notebook.page.read`; an audit failure prevents metadata disclosure. Create auditing co-commits with the page and revision. Audit metadata never includes titles or draft/device identifiers. Responses use existing no-store behavior. Cross-patient page IDs return 404 when the route patient is authorized, otherwise authorization fails first.

## Persistence and files

`NotebookPage` and metadata-only `NotebookRevision` use private setters, immutable identifiers, restrictive foreign keys (including revision author to Identity), page SQL rowversion, positive revision checks, unique page/revision numbers, optional per-page draft uniqueness, and patient/time, patient/visit and page/time indexes.

One additive migration: `20260919160918_AddNotebookFoundation`. It creates only NotebookPages and NotebookRevisions with their indexes and constraints. Generated only; do not apply to the main ClinicDb as part of this slice. SQL integration fixtures migrate randomly named disposable `ClinicTests_*` databases only.

Changed/added files for this slice:

- Domain: `Entities/NotebookPage.cs`, `Entities/NotebookRevision.cs`, `Enums/NotebookEnums.cs`.
- Application: `Notebook/INotebookStore.cs`, `Notebook/NotebookModels.cs`, `Notebook/NotebookService.cs`.
- Infrastructure: `Repositories/NotebookStore.cs`, `Persistence/NotebookMapping.cs`, `Persistence/ClinicDbContext.cs`, `Authentication/StaffAuthentication.cs`, migration + designer + model snapshot.
- API: `Controllers/StaffNotebookPagesController.cs`, `Notebook/NotebookModels.cs`, `Program.cs`.
- Tests: `Clinic.IntegrationTests/Api/StaffNotebookPagesHttpTests.cs`, `xUnit Test Project/NotebookDomainTests.cs`.
- This document. Earlier patient-context and Flutter working-tree changes are outside this slice.

## Verification

- `dotnet build backend/Clinic/Clinic.slnx`: succeeded, zero warnings/errors.
- `dotnet test backend/Clinic/Clinic.slnx --no-build`: 273 unit and 690 integration tests passed, zero failures/skips.
- EF `has-pending-model-changes`: no pending changes.
- Notebook coverage includes cookie/MFA and mobile bearer paths, invalid bearer downgrade prevention, persisted grant removal, Doctor create/read/list, assistant read-only, Receptionist denial, exact patient scope and cross-patient routing, visit ownership, pagination bounds, server-owned fields, rowversion, audit events/fail-closed reads/atomic create rollback, revision uniqueness, staff FK and restrictive deletion.

## Deferred work

No revision upload, StoredFile/IFileStorage integration, ink payloads, finalization/amendment transitions, Flutter changes, drawing or offline drafts. Rowversion is returned for future optimistic concurrency; this slice exposes no page update API. Visit/patient consistency is validated by the service using the existing immutable Visit.PatientId convention; the visit FK itself references Visit.Id.
