# Secure file storage — Phase 2: patient/visit attachment links + protected staff API

Phase 2 builds the clinical attachment surface on the Phase 1 foundation (docs/file-storage-
phase-1.md): one explicit link aggregate, persisted permissions, five protected staff endpoints
with fail-closed auditing, and a documented upload consistency model. Phase 3 feature rollout
(labs/imaging, archive/OCR, notebook, prescription persistence, patient uploads, S3, malware
scanning, orphan reconciliation) is deliberately NOT started. One additive migration
(`AddPatientAttachments`) is generated but NOT applied; ClinicDb was not touched.

## PatientAttachment model

`PatientAttachment` (Domain) is the create-once clinical visibility link: Id, PatientId
(required), StoredFileId (required, UNIQUE — one physical StoredFile can be clinically visible
for exactly one patient), VisitId (nullable), CreatedAtUtc (TimeProvider/UTC), CreatedByStaffId
(required — uploads are staff-only). No file metadata is duplicated: OriginalFileName,
ContentType, SizeBytes, Sha256 and StorageKey live only on StoredFile; list/download responses
project them through the link. There is no polymorphic ResourceType/ResourceId ownership —
explicit patient/visit relationships only. No Status column: no staged-upload workflow exists.

## Database relationships / delete behavior

`PatientAttachments` table: PK Id; FK → Patients, StoredFiles, Visits — all
`DeleteBehavior.Restrict`. No cascade can ever remove clinical attachment history; attachments
are create-once with no update/delete surface. Indexes: UNIQUE StoredFileId, (PatientId,
CreatedAtUtc), (VisitId, CreatedAtUtc). Uploaded bytes NEVER enter SQL (no byte[] column) and
never enter logs (no content/filename/key logging anywhere in the feature).

## Permissions / roles

Persisted permission claims `attachments.read` and `attachments.write` were added to the
`StaffAuthentication.Permissions` catalog (declaration only — nothing is granted automatically;
existing accounts gain nothing until an operator grants them via set-grants). The existing
claim-type-based per-request revalidation covers them with no code change: removing
attachments.read/write or a patient_record_id scope invalidates the cookie immediately
(HTTP test proves removal → 401 on an existing session).

Policy (narrow, no Admin role): READ and WRITE both require authenticated user + `amr=mfa` +
role Doctor or DoctorAssistant + the explicit permission + exact persisted patient scope.
Receptionist never gains clinical attachment access.

## Visit authority rule (exact)

- Patient-level upload: patient scope + attachments.write (+ MFA). No doctor association needed.
- Visit-level upload: the visit must exist and belong to the route patient (hidden 404
  otherwise). For **Doctors**, the controller additionally enforces the existing clinical
  authority rule: persisted `StaffUser.AssociatedDoctorId == Visit.DoctorId` (queried live per
  request; appointment/schedule claims and client-supplied IDs never qualify). For
  **DoctorAssistants**, no association is required — consistent with the existing assistant
  clinical-write policy (vitals): assistant authority is patient scope + permission + MFA.
  Existence/scope checks run before any byte streams.

## Endpoints

- `POST /api/staff/patients/{patientId}/attachments` — multipart/form-data, exactly one file
  part named `file`.
- `POST /api/staff/patients/{patientId}/visits/{visitId}/attachments` — same + visit authority.
- `GET /api/staff/patients/{patientId}/attachments?page&pageSize` — bounded list.
- `GET /api/staff/patients/{patientId}/visits/{visitId}/attachments` — visit-scoped list.
- `GET /api/staff/patients/{patientId}/attachments/{attachmentId}/download` — protected stream.

No storage key, path, or hash is ever accepted from or returned to clients. No public/static
URLs, no signed URLs, no anonymous routes. List/download are reads (no mutation CSRF);
uploads are cookie-authenticated mutations behind the standard X-CSRF-TOKEN check
(`invalid_csrf_token` on failure, before any storage or database write).

## File policy (honest limits)

Only PDF (`.pdf` + `application/pdf` + `%PDF-`), JPEG (`.jpg`/`.jpeg` + `image/jpeg` +
`FF D8 FF`), PNG (`.png` + `image/png` + 8-byte PNG signature) are accepted; extension,
declared MIME and pairing are validated eagerly, and the leading magic bytes are verified
while streaming (`ValidatingAttachmentStream` — at most one small prefix buffer, never a
whole-file read; a fake PDF built from HTML bytes is rejected 415). This is a narrow format
gate, NOT antivirus/malware safety. Downloads are always `Content-Disposition: attachment`
(ASCII `filename` fallback + RFC 5987 `filename*` for Unicode/Arabic names; control
characters/quotes/backslashes stripped — CRLF injection impossible) with
`X-Content-Type-Options: nosniff` and `no-store`. Range/resume downloads are deferred;
malware scanning remains a future requirement.

## Size limits

`Attachments:MaxFileSizeBytes` (default 10 MiB) is validated at startup: 1..1 GiB and never
above `FileStorage:MaxFileSizeBytes` (25 MiB development default). The validating stream
enforces the feature limit during streaming; the Phase 1 storage counter (actual bytes, never
Content-Length) remains the authoritative final enforcement and the storage absolute maximum
the hard backstop. Oversize → 413 `file_too_large`; empty → 400; unsupported → 415
`unsupported_file_type`.

## Upload consistency model

`AttachmentService.UploadAsync`:
1. Cheap validation first (patient exists; visit belongs to patient) — a doomed request never
   streams bytes (proven with an unreadable stream).
2. Policy gates (extension/MIME eager; signature + feature size while streaming).
3. Stage into private storage (opaque staging key; hash + exact size measured in one pass).
4. Generate final opaque key; create StoredFile + PatientAttachment (server facts only).
5. Promote the object (atomic move).
6. ONE shared `SaveChanges` commits StoredFile row + PatientAttachment row + the `file.upload`
   audit event (appended via the existing `IAuditMutationWriter` mutation scope) — so a
   successful upload is always ALL of: object bytes, StoredFile metadata, PatientAttachment
   link, successful AuditEvent.

Failure behavior (all HTTP-tested):
- stage/promote/policy failure → no rows, no audit event, staged bytes cleaned.
- shared save failure (including an injected audit-insert failure) → nothing committed (single
  SaveChanges), rejected rows detached by the store, the promoted object deleted as
  compensation; the response is a sanitized 500. HTTP test proves: no visible attachment, no
  StoredFile, no file.upload event, and the object gone.
- audit-insert failure therefore cannot leave a committed attachment: the audit event shares
  the attachment save's transaction.
- residual, documented honestly: a hard crash between promotion and commit can orphan an
  object (no metadata/link/audit — reconciliation is a future operational concern), and a
  failing compensating delete leaves the same orphan while the original failure propagates.
- one upload request = exactly one `file.upload` event (no separate file.link).

## Audit event catalog (all PatientId populated → existing audit.patient.read scoping works)

| Action | Trigger | ResourceType | ResourceId | PatientId | Metadata |
|---|---|---|---|---|---|
| file.upload | successful upload | attachment | PatientAttachment.Id (N) | route patient | none |
| file.list | successful list (ONE per request) | patient or visit | route patient/visit Id (N) | route patient | none |
| file.download | successful download | attachment | PatientAttachment.Id (N) | route patient | none |

`file.link`/`file.remove` are reserved for future independent operations. Denials (401/403,
hidden 404, CSRF, validation) and missing backing objects produce NO Succeeded events. All
events flow through the existing AuditEvents schema — no audit migration.

## Download flow and cross-patient protection

authenticate/MFA/role/permission/scope → resolve PatientAttachment by (route patient,
attachment id) → open backing object → persist `file.download` (fail-closed) → only then
stream. A foreign patient's attachment requested through the requester's own patient route is
a hidden 404 `attachment_not_found` (never 403, never a successful event) — matching the
project's hidden-resource convention. A missing backing object (metadata without bytes) is a
sanitized 503 `backing_object_missing`: never 200, never empty bytes, never a path disclosure,
no download event.

## Lists

Bounded pagination (page 1–1000, pageSize 1–100, default 20) ordered `CreatedAtUtc DESC, Id
DESC`. Offset pagination is documented behavior: attachments are create-once, so new uploads
shift pages; a cursor was deliberately not added absent a demonstrated need. Projections expose
only attachmentId, visitId, originalFileName, contentType, sizeBytes, createdAtUtc — never
StorageKey, hash, actor, provider or entities.

## What Phase 2 deliberately does NOT include

No Labs/Imaging/Archive/OCR/Notebook/prescription attachment integration, no patient mobile
uploads/OTP, no Flutter/UI, no S3 provider, no malware scanning, no user-facing delete/update
of attachments, no orphan reconciliation job, no range downloads, no automatic permission
grants. Duplicate identical uploads are allowed and independent (no SHA-256 dedup — identical
bytes may be two different clinical records; each gets its own StoredFile/attachment/key).

## Initial implementation verification (before final hardening gate)

- Build: 0 warnings, 0 errors.
- Unit: 221 passed (24 new: policy matrix, signature fakes across 1-byte chunks, feature size
  streaming, filename path-stripping incl. UNC/Windows/relative, upload success/audit shape,
  duplicate independence, unreadable-stream pre-validation, save-failure compensation).
- Integration: 17 new StaffAttachmentHttpTests — upload (patient/visit, exact bytes, exactly
  one event, no storage internals in responses, doctor authority, cross-patient hidden 404,
  unsupported/fake-type 415, feature-limit 413, empty 400, CSRF missing/invalid store nothing,
  scope/role/MFA/permission 403 matrix with zero audit writes, audit-insert failure → nothing
  visible and object compensated), list (pagination, isolation, no internals, one event per
  request, fail-closed), download (exact bytes, attachment disposition + nosniff + no-store,
  Arabic filename*, exactly one event, audit failure → no bytes, missing object 503, foreign
  hidden 404, revoked permission → 401).
- EF has-pending-model-changes clean after `AddPatientAttachments`; migration is additive
  (one new table, three Restrict FKs, unique StoredFileId, two composite indexes; no existing
  table touched; Down drops only the new table). NOT applied. `git diff --check` clean.

## Final pre-commit hardening gate (2026-09-17)

Upload SQL atomicity is one shared SaveChanges after StoredFile, PatientAttachment, and
file.upload have all been added. Independent SQL failures at each of the three INSERTs
are tested with MaxBatchSize(1), a save observer, fresh-context direct StoredFile queries
(not a join that could hide orphans), and checks for removed promoted/staged objects.
Cleanup also covers exceptions from adding metadata or appending audit after promotion.
A process crash or failing compensation can still leave an orphan object, never an upload
whose StoredFile row committed separately from its attachment link.

The validating stream now drains every buffered signature byte, including when consumers
request one byte at a time. Tests exercise genuinely non-seekable one-byte sources, small
destination buffers, exact storage bytes, SHA-256, byte count, cancellation and read errors.
PDF/JPEG/PNG signatures are required; empty and truncated signatures are rejected.
Extensions and MIME case are insensitive. Content-Type parameters are deliberately rejected
by the narrow exact MIME allowlist (415); no implicit media-type parameter normalization.

Both POST endpoints use AttachmentUploadGate before multipart model binding. The total
request cap is Attachments:MaxFileSizeBytes + 65,536 bytes (default 10,551,296 bytes), set
on the server request-size feature when writable and independently enforced by an actual-byte
counting request stream. This also bounds unknown-length/chunked and multi-part bodies;
Content-Length is not the enforcement mechanism. MultipartBodyLengthLimit uses the same
cap; fields are bounded to 65,536 bytes, 16 values, and 16,384 bytes of multipart headers.
The application validator still rejects actual file bytes above MaxFileSizeBytes (default
10,485,760). MVC rejects request-limit failures during form binding with 400; server-level rejection can be 413. No unrelated endpoint or global request limit was changed.
Header-only CSRF and patient/visit authority run before model binding. Tests measure zero
request-body reads on missing/invalid CSRF and known cross-patient visit rejection.

IFormFile model binding is framework-buffered: files above the default 64 KiB memory
threshold may spool to ASPNETCORE_TEMP, or the process user's temporary directory when unset.
This is not a claim that files never touch disk before private storage. The framework owns
and disposes its request buffers; project code streams to private staging without a whole-file
memory copy. Configure framework temporary storage as private (never a public/static directory).
The provider root is also rejected when equal to or beneath wwwroot, including the default
wwwroot location when no web root currently exists. Request cancellation propagates through
reads and storage; the service owns/disposes opened upload streams even on early rejection.
See [ASP.NET Core upload buffering documentation](https://learn.microsoft.com/en-us/aspnet/core/mvc/models/file-uploads?view=aspnetcore-10.0).

Download Succeeded means the server authorized/resolved the object, opened it successfully,
and approved disclosure after durable audit persistence. It does NOT establish that the client
received every byte: disconnect/cancellation after audit can interrupt delivery. Tests confirm
authorize -> metadata -> open -> audit -> response construction, disposal on audit failure,
no body or success disposition before audit, and no success event for a missing object.
Attachment disposition tests cover Arabic, spaces, both quotes, semicolons, percent, emoji,
CR/LF, separators and long names. Upload metadata rejects control characters and names above
255 characters; disposition sanitization/fallback remains defense in depth.

Offset pagination remains appropriate for these bounded attachment lists. It is deterministic
(CreatedAtUtc DESC, Id DESC), but new uploads can shift subsequent pages; no historical traversal
guarantee is claimed. Immediate cookie revalidation is tested separately for removal of
attachments.read, attachments.write, and patient_record_id scope. Doctor authorization reads
persisted AssociatedDoctorId; appointment/schedule grants and client doctor values cannot
replace equality with Visit.DoctorId. DoctorAssistant retains patient scope + permission + MFA.
