# Staff MVC — Clinical tests Phase 1

Status: **READY FOR STAFF MVC CLINICAL TESTS PHASE 1 REVIEW**.

This slice retains file-first results. It adds patient-scoped staff pages for requesting a
test, uploading result files and reviewing the request. It does not add structured results.

## Host and service boundaries

This checkout has one ASP.NET Core web host, `Clinic.Api`, and did not contain a separate staff
MVC project or Razor views. MVC views now run in that existing host with `AddControllersWithViews`.
No separate login system or HTTP calls from MVC to the REST API were introduced. Entry requires
an existing staff cookie session established through the existing authentication/MFA workflow.
A portal login screen, navigation shell and patient search are outside this slice.

`ClinicalTestService` creates, lists and reads requests. Two narrow read projections provide
patient identity and visit choices. `ClinicalTestLifecycleService` owns upload/review state,
live doctor authority and concurrency. Its authority check also controls review-button visibility.
The existing attachment workflow owns file validation, private storage, compensation and mutation
audits. MVC has no DbContext, lifecycle transitions, doctor-association comparisons, file-signature
validation, storage-key decisions or mutation audit construction.

## Routes

All MVC routes are rooted at `/staff/patients/{patientId:guid}/tests`.

| Method | Suffix | Page/action |
| --- | --- | --- |
| GET | (none) | Patient history; category/status/page/pageSize query filters |
| GET | `/create` | Doctor request form |
| POST | `/create` | Create through the application service |
| GET | `/{requestId:guid}` | Detail, result files and available actions |
| POST | `/{requestId:guid}/results` | Upload exactly one result file |
| POST | `/{requestId:guid}/review/start` | Start review |
| POST | `/{requestId:guid}/review/complete` | Complete review |

Downloads link to the existing protected
`GET /api/staff/patients/{patientId}/attachments/{attachmentId}/download` route. This is a browser
navigation, not a loopback REST call. Private files are never under the static asset directory.

## Authorization and patient context

All pages/actions use `StaffSession`: the existing staff cookie, MFA, live persisted session/
permission revalidation and approved staff roles. Each operation also applies the existing
patient-resource authorization policies with the exact route patient ID.

| Operation | Role | Explicit permission |
| --- | --- | --- |
| List/detail | Doctor, DoctorAssistant | `tests.read` |
| Create page | Doctor | `tests.read` and `tests.write` |
| Create POST | Doctor | `tests.write` plus persisted clinical doctor authority |
| Upload | Doctor, DoctorAssistant | `tests.write` and `attachments.write`; Doctor authority is service-checked |
| Review | Doctor | `tests.write` plus persisted clinical doctor authority |
| Attachment metadata/download | Doctor, DoctorAssistant | `attachments.read` |
| Optional visit picker | Doctor | `visits.read` in addition to create-page permissions |

Receptionist cannot enter the clinical-test pages or execute these mutations. Buttons reflect
permissions and applicable state, but POST authorization remains authoritative. Mutations retain
the backend's write-permission contract; staff need `tests.read` to use their redirected detail page.
No implicit attachment-read grant accompanies test-read or test-write permissions.

Every clinical page shows the patient's name and MRN; legacy records without an MRN show their
opaque patient reference. No phone, address, DOB, staff-account ID or unrelated patient record
fields are projected. This minimal context belongs to the authorized, audited test page; it does
not grant access to the patient administration module. Foreign patient IDs fail authorization;
foreign request IDs under an authorized patient return unavailable.

## List and create workflow

History uses the existing newest-first order (requested time, then ID), default page size 20,
server bounds of 1–100 records/page and pages 1–1000. The table shows name, localized category/
status, requested UTC time, result count and reviewed UTC time. Category/status filters reset the
page. A full page offers Next; a final full page may therefore lead to an empty page with Previous.

The request form posts category, name and optional visit/instructions. Category/name validation
and text limits are checked server-side, then the shared service validates the request. The server
sets requesting doctor, time and Requested state. Creation has no pre-existing RowVersion token.
The visit picker is shown only when authorized choices exist; its query is limited to the latest
100 visits for this patient and the actor's live associated doctor. There are no visit notes in
the picker. Posted VisitId is independently checked by the create service, including cross-patient
and wrong-doctor rejection.

Successful creation redirects to detail. Invalid form input redirects to a fresh form with a
localized validation notice; entered clinical text is not stored in TempData/cookies. Native
required-field/length checks provide immediate feedback, but do not replace service validation.

## Upload, concurrency and review

Detail displays safe test data and counts. Attachment names, IDs, MIME types, sizes, timestamps
and download controls are emitted only with `attachments.read`. A test-only reader gets the count
and a permission explanation, with no attachment metadata in HTML or hidden fields.

The upload form accepts one PDF/JPEG/PNG and displays the configured size limit. The shared
`AttachmentUploadGate` checks header CSRF, permissions, patient/request binding, doctor authority
and immutable state **before reading multipart bytes**. It applies the existing request-stream
limit. MVC then parses the bounded form explicitly and delegates the stream to the application
workflow. No MIME/signature or lifecycle rules are reimplemented in MVC.

The small local script sends FormData with `X-CSRF-TOKEN`. JavaScript is required for uploading;
the upload button starts disabled and a noscript explanation is provided. Using a hidden CSRF
field alone would permit antiforgery's form-reading fallback before verification, so it is not an
upload fallback. Create/review use ordinary POST forms with server-generated antiforgery tokens.

Upload and both review forms carry the current base64 SQL RowVersion. Conflicts redirect to a fresh
detail read with “The test request changed. Refresh and try again.” New state/tokens replace the
submitted state; there is no overwrite/retry with a stale token. Known form/file/state failures
also redirect with catalog-key-only TempData. CSRF/authentication/authorization/not-found failures
retain safe HTTP error statuses and disclose no patient context. Failed AJAX uploads display safe
localized messages rather than inserting server response HTML.

Successful upload/review is POST → redirect → GET. Fetch uses `redirect: manual`, then navigates to
the server-rendered detail URL; it does not perform one audited GET in fetch followed by a second
browser GET. Submit controls prevent repeated clicks while submitting. Review uses dedicated
Start Review (Uploaded) and Complete Review (UnderReview) forms. Requested has no review action;
Reviewed has no mutation controls. A completion notice explains that further uploads/changes are
unavailable. All actual transitions, reviewer/time fields and final immutability remain in the
existing application/domain workflow.

## Downloads, errors and auditing

Download links invoke the existing patient-bound attachment endpoint, which checks persisted
authorization, opens private storage and records `file.download` before streaming any bytes.
Its attachment Content-Disposition and nosniff behavior are unchanged. The browser downloads the
file; inline embedding, public URLs and storage-key links were not added. Download endpoint failures
retain its existing sanitized API error response rather than introducing a second download adapter.

Staff pages return `Cache-Control: no-store`, `Referrer-Policy: no-referrer`, nosniff and a restrictive
self-only script/style CSP with frame/object blocking. Data images are permitted for Bootstrap's
built-in select-control graphics. Assets are local; pages make no CDN requests.
Safe localized HTML maps 400/401/403/404/409/413/415/500/503 cases. Unexpected failures log only the
exception type in the staff boundary and return generic text, never SQL, paths, keys or stack traces.

Reads use the existing `HttpAccessAudit` before rendering clinical content:

- List: one `test-request.list` for the patient.
- Detail: one `test-request.read` for the request, including permitted attachment metadata.
- Create GET: one `test-request.list` for the patient-scoped request context, plus `visit.list` when
  authorized visit choices are queried. These are page reads, not create mutation events.

Mutation services remain the sole source of `test-request.create`, `file.upload`,
`test-request.result.upload`, `test-request.review.start` and `test-request.review.complete`.
Redirected page reads have their own expected read event. Audit persistence failure prevents
clinical page output; mutation audit failure rolls back through the existing service/transaction.
No new action codes or sensitive audit metadata were introduced.

## Localization, accessibility and presentation

Arabic/RTL is the default for `/staff` only. `?culture=en` selects English/LTR; navigation/forms
carry the selected culture. API localization behavior is unchanged. The small shared `StaffText`
catalog contains bilingual UI/status/error labels and can later move behind resource files.
Clinical text is not translated. Razor encodes text; BDI/dir=auto preserve mixed Unicode names and
filenames. Times explicitly show UTC. File sizes adapt to B/KB/MB.

Bootstrap 5.3.8 LTR/RTL CSS and its MIT license are vendored locally. The layout has a persistent
patient banner, textual status badges, labeled fields, semantic table headings/captions, a skip
link, focus indicators, alert notices and keyboard-operable native forms. Tables scroll horizontally
on small viewports. Native file-picker labels follow the browser/OS language.

## Verification

42 new SQL-backed MVC integration cases cover role/MFA/permission/patient scope, read auditing,
metadata suppression, paging bounds, actual rendered-form CSRF, server doctor identity, Unicode/
HTML encoding, scoped visit choices, rejected cross-patient visits, doctor/assistant uploads,
wrong file type, multiple files, size limits, stale tokens, Reviewed immutability, review actions,
assistant rejection, protected download links, single mutation audit counts, revoked persisted
grants, fail-closed page reads and mutation-audit rollback. A body meter proves denied uploads
consume **zero multipart bytes**. Local CSS/JS delivery and both language variants are tested.

Synthetic Razor HTML for list/create/detail in both languages can optionally be exported by setting
`CLINIC_MVC_SNAPSHOT_DIR` for the MVC test run. Antiforgery tokens are replaced in exported copies.
Browser visual inspection used those disposable synthetic snapshots; it is not a claim of a live
browser login/upload end-to-end test. Arabic list/detail and English create layouts were inspected.

Final verification commands/results:

- `git diff --check`: clean (only repository line-ending notices).
- `dotnet build backend/Clinic/Clinic.slnx --nologo -m:1 -p:UseSharedCompilation=false`:
  succeeds with zero warnings/errors.
- `dotnet test backend/Clinic/Clinic.slnx --no-build --no-restore --nologo -m:1 --verbosity minimal`:
  **889 passed: 269 unit + 620 integration; zero failed, zero skipped**.

The tests migrate only disposable `ClinicTests_*` databases. No new MVC migration, ClinicDb
migration/data changes, commit or push were performed. The pre-existing uncommitted Phase 2
implementation remains in the working tree.

## Files changed in this MVC slice

Paths below are repository-relative; Phase 2-only changes are inventoried in the Phase 2 documents.

- `backend/Clinic/src/Clinic.Api/Program.cs`
- `backend/Clinic/src/Clinic.Api/Controllers/StaffTestsMvcController.cs`
- `backend/Clinic/src/Clinic.Api/StaffMvc/StaffTestModels.cs`
- `backend/Clinic/src/Clinic.Api/StaffMvc/StaffText.cs`
- `backend/Clinic/src/Clinic.Api/StaffMvc/StaffUiErrors.cs`
- `backend/Clinic/src/Clinic.Api/Views/_ViewImports.cshtml`
- `backend/Clinic/src/Clinic.Api/Views/_ViewStart.cshtml`
- `backend/Clinic/src/Clinic.Api/Views/Shared/_StaffLayout.cshtml`
- `backend/Clinic/src/Clinic.Api/Views/Shared/_Patient.cshtml`
- `backend/Clinic/src/Clinic.Api/Views/StaffTestsMvc/Index.cshtml`
- `backend/Clinic/src/Clinic.Api/Views/StaffTestsMvc/Create.cshtml`
- `backend/Clinic/src/Clinic.Api/Views/StaffTestsMvc/Detail.cshtml`
- `backend/Clinic/src/Clinic.Api/wwwroot/staff-assets/tests.css`
- `backend/Clinic/src/Clinic.Api/wwwroot/staff-assets/tests.js`
- `backend/Clinic/src/Clinic.Api/wwwroot/vendor/bootstrap/bootstrap.min.css`
- `backend/Clinic/src/Clinic.Api/wwwroot/vendor/bootstrap/bootstrap.rtl.min.css`
- `backend/Clinic/src/Clinic.Api/wwwroot/vendor/bootstrap/LICENSE`
- `backend/Clinic/src/Clinic.Application/ClinicalTests/ClinicalTestModels.cs`
- `backend/Clinic/src/Clinic.Application/ClinicalTests/ClinicalTestService.cs`
- `backend/Clinic/src/Clinic.Application/ClinicalTests/ClinicalTestLifecycleService.cs`
- `backend/Clinic/src/Clinic.Infrastructure/Repositories/ClinicalTestStore.cs`
- `backend/Clinic/tests/Clinic.IntegrationTests/Api/StaffClinicalTestsMvcTests.cs`
- `docs/staff-mvc-clinical-tests-phase-1.md`
- `docs/progress-2026-09-18.md`

## Deferred and next step

Deferred: structured results, OCR, notifications, patient result release/patient UI, Doctor tablet,
archive/notebook, appointments/medications redesign, a full portal navigation/login UI, inline file
preview, live browser end-to-end automation and deployment/migration application.

Next: **STAFF MVC CLINICAL TESTS PHASE 1 REVIEW / HARDENING**. Stop after this slice.
