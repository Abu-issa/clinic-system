# Shared audit trail — Phase 3 read/download access auditing and query authorization

Phase 3 continues the uncommitted handoff on Phase 2 commit `71bf409`. It adds durable
access auditing for sensitive clinical reads and the prescription PDF download, best-effort
security auditing for successful authentication events, and a protected audit-query endpoint.
No schema migration is required. ClinicDb was not migrated or otherwise changed during this
work. Tests use disposable `ClinicTests_*` databases.

## Read and download audit coverage

`HttpAccessAudit.RecordAsync` (Clinic.Api/Audit/HttpAccessAudit.cs) is called by controllers
after a successful service result and before the protected body is written. It resolves the
actor from the validated `staff_id` claim (NameIdentifier fallback), stamps `no-store`, and
persists exactly one `AuditEvent` with `Outcome = Succeeded`, the HTTP trace identifier
(bounded to 128), and the GUID resource ID in the opaque `N` format. Persistence runs through
`IAccessAuditWriter` (`AccessAuditWriter`), which creates its own short-lived `ClinicDbContext`
so the access event commits independently of any scoped business context before protected data
is returned. Authorization always precedes the audit call; no response body, DTO or error
detail is copied into metadata.

| Operation | ActionCode | ResourceType | ResourceId | PatientId |
|---|---|---|---|---|
| GET /api/staff/patients/{patientId} | patient.record.read | patient | patient N-format | route patient |
| GET /api/staff/patients/{patientId}/medical-profile | patient.clinical-profile.read | patient | patient N-format | route patient |
| GET /api/staff/patients/{patientId}/visits | patient.visits.read | patient | patient N-format | route patient |
| GET /api/staff/patients/{patientId}/visits/{visitId} | visit.read | visit | visit N-format | route patient |
| GET /api/staff/patients/{patientId}/prescriptions | patient.prescriptions.read | patient | patient N-format | route patient |
| GET /api/staff/patients/{patientId}/visits/{visitId}/prescriptions | patient.prescriptions.read | patient | patient N-format | route patient |
| GET /api/staff/patients/{patientId}/prescriptions/{prescriptionId} | prescription.read | prescription | prescription N-format | route patient |
| GET /api/staff/patients/{patientId}/prescriptions/{prescriptionId}/pdf | prescription.pdf.download | prescription | prescription N-format | route patient |
| GET /api/staff/doctors/{doctorId}/appointments/{appointmentId}/rescheduling | appointment.read | appointment | appointment N-format | appointment's patient |

The appointment-read event required the appointment's patient ID; `AppointmentReschedulingDetails`
gained an internal `[JsonIgnore] PatientId` property supplied by `AppointmentReschedulingService`,
so the response DTO is unchanged. Advisory rescheduling slot availability (`GET .../availability`)
is not patient clinical data and is not audited. Medication catalog reads, session validation
(`GET /api/staff/auth/session`) and CSRF-token issuance remain unaudited by deliberate scope
(see limits).

## Fail-closed read/download policy

For every operation in the table above, an `AuditEvent` persistence failure prevents disclosure
of the protected payload: exceptions from `RecordAsync` propagate to the existing sanitized
global handler before any protected bytes are written. The integration suite injects an
`INSERT INTO [AuditEvents]` failure per operation and asserts a 500 `unexpected_error`
Problem Details response that contains no clinical content ("Sensitive*" markers absent), no
exception text and, for the PDF route, no `%PDF` prefix and no content disposition; the
previously successful event is not duplicated. The failed request itself creates no audit row.

Authentication availability is deliberately independent of this policy: `HttpAccessAudit.SecurityAsync`
is best-effort (below), and the underlying writer does not swallow failures — controllers choose
the policy by calling `RecordAsync` (propagating) or `SecurityAsync` (catch-and-log).

## Prescription PDF download audit

`GET /api/staff/patients/{patientId}/prescriptions/{prescriptionId}/pdf?language=ar|en`
authorizes first (Doctor + prescriptions.read + MFA + exact persisted patient scope, unchanged
from the details endpoint), prepares the printable PDF through the existing print service, then
persists `prescription.pdf.download` and only afterwards streams the bytes. Metadata carries
exactly one key, `language`, with the validated whitelisted value `ar` or `en`. Failure
injection proves the prescription lifecycle is untouched by any path: status remains Finalized,
RowVersion is unchanged, there is no implicit Release, and the response stays sanitized.
Repeated successful downloads legitimately create separate events (verified: two downloads →
two events, both with the correct language metadata).

## Authentication audit (best-effort)

`HttpAccessAudit.SecurityAsync` records successful identity transitions with
`ResourceType = staff-account`, `ResourceId` = the actor's own staff ID, `PatientId = null`,
no metadata, and the request trace ID. Persistence failures are caught and logged
("Security audit persistence failed"); login/logout behavior is unchanged. There is no queue
or durable-delivery claim after such a failure — operators must monitor the error signal.
The covered events:

| Transition | ActionCode |
|---|---|
| Password accepted (first factor) | staff.password.accepted |
| Enrollment setup view | staff.enrollment.setup |
| Enrollment completed | staff.enrollment.completed |
| TOTP login completed | staff.mfa.completed |
| Recovery-code login completed | staff.recovery.login |
| Logout (authenticated) | staff.logout |
| Self service-session revocation | staff.sessions-revoke |

`staff.sessions-revoke` was already emitted by the Phase 2 administrative `ChangeAsync: revoke`
path; the HTTP self-revocation now emits the same ActionCode with the acting staff user as the
resource. No secrets are ever emitted: the suite serializes all persisted identity events and
asserts the absence of the TOTP key, recovery codes, password and account name. Under a full
audit outage, password login, enrollment, session validation and logout still succeed (the
outage test counts four suppressed attempts), no partial events persist, and the error is logged.
Failed credential attempts are deliberately not audited in this phase: unknown-user and
wrong-password failures return byte-identical neutral 401s, produce zero audit writes, and
Identity lockout still engages.

## Audit query API

Single endpoint: `GET /api/staff/audit-events` (`StaffAuditEventsController`), `no-store`,
protected by the `AuditRead` policy: authenticated user + `amr=mfa` + `Doctor` role + an
explicit persisted `audit.patient.read` or `audit.admin.read` permission claim. Both
permissions are registered in the `StaffAuthentication.Permissions` catalog (declaration only;
nothing is granted automatically), so the existing claim-type-based per-request revalidation
applies: removing either permission (or a `patient_record_id` scope claim) from the persisted
grants invalidates the existing cookie immediately (401 on the next request) — regression-tested.

- Patient-scoped reading (`audit.patient.read`): the effective scope is the caller's persisted
  `patient_record_id` claims; only events whose `PatientId` is inside that scope are returned.
  An explicit `patientId` filter must be inside the scope, otherwise 403 before any read or write.
- Non-patient/administrative reading (`audit.admin.read`): returns only events with
  `PatientId = null` (administrative events). It grants no patient visibility: an explicit
  `patientId` filter is rejected 403 because it is not within any patient scope.
- A caller holding both permissions gets the union of both rule sets.

Query parameters: `fromUtc`/`toUtc` (defaults: last 7 days ending now UTC; maximum 31-day
window; `to` must exceed `from`), `actorStaffId`, `actionCode`, `resourceType`, `resourceId`,
`patientId`, `outcome`, `page` (1–1000, default 1), `pageSize` (1–100, default 50). Length
bounds reject oversized filter strings with a sanitized 400 `invalid_audit_query` before any
database write. `AuditQueryStore` re-asserts the same bounds for direct application callers.
Results are ordered deterministically by `OccurredAtUtc` descending, then `Id` descending, and
paged with a `Take(pageSize + 1)` probe surfaced as `hasMore`; the response is an explicit DTO
projection (`items`, `page`, `pageSize`, `hasMore`), never tracked entities.

Successful queries are themselves audited: `audit.query` / `audit` / `events`, with the
requested `patientId` filter (or null) as the event's PatientId and no metadata, fail-closed
like every other read. Note the consequence: a `patientId`-less query by a patient-scoped
reader writes a null-PatientId (administrative) self-event that only `audit.admin.read` holders
can later see.

## Denied and failed request behavior

Authorization precedes auditing everywhere. Anonymous requests (401), missing MFA/permission/
scope (403), hidden cross-patient mismatches (404) and CSRF or validation failures create no
audit events — proven with the failure interceptor armed (zero INSERT attempts). Failed
attempts are still not written as `Failed` events in this phase. Failed logins are not audited
and cannot amplify into extra writes before authentication succeeds.

## Duplicate event review

Each successful request writes exactly one event: the sensitive-read suite asserts
`Assert.Single` per actor per request, the query suite counts two `audit.query` events for two
successful paginated calls (the rejected 403 filter page writes nothing), and two PDF downloads
write exactly two download events. Phase 2 mutation events remain one-per-command.

## Metadata safety

The complete Phase 3 metadata-key catalog:

| Key | Values |
|---|---|
| language | Prescription PDF language: `ar` or `en` (whitelisted query token) |

Every other Phase 3 event carries an empty metadata dictionary. No response bodies, names,
diagnoses, notes, medication text, identifiers beyond the opaque resource ID, query strings,
headers, cookies, tokens, passwords, OTP/TOTP material, recovery codes or security stamps are
ever emitted. The Phase 1 metadata validator (bounded counts, lengths, machine-safe values,
credential-smelling key rejection) remains in force for these writes.

## Deliberate limits and deferred work

- Failed/denied attempts (401/403/404/CSRF/validation) are not recorded as `Failed` events.
- Failed logins are not audited (including lockout-eligible attempts); only successful
  identity transitions are.
- Medication catalog reads, auth session validation, CSRF issuance and appointment-slot
  availability queries are not audited.
- Retention (archival/deletion of old events) remains unresolved; the store is append-only
  with no pruning policy.
- There is no audit query or export UI, CSV/PDF export, alerting, or saved-search facility —
  API only, Doctor-only, MFA-gated.
- Best-effort security auditing has no retry/queue; a suppressed failure is observable only
  through the error log.
- Production requires protected database storage, encrypted and tested backups, restricted
  operator access, and a documented retention/legal-hold procedure before clinical use; audit
  events must be included in that backup scope.
- Phase 2 mutation auditing is unchanged; its catalog, transaction boundaries and guarantees
  are documented in audit-trail-phase-2.md.

## Verification (2026-09-16)

- Full suite: 170 unit + 359 integration = 529 passed, 0 failed, 0 skipped.
- Targeted: AccessAuditHttpTests 28 passed; StaffIdentityHttpTests (incl. the three Phase 3
  identity-audit cases) 52 passed; Phase 2 AuditMutation/AuditPersistence suites 39 passed.
- Build: 0 warnings, 0 errors. `git diff --check`: clean.
- EF has-pending-model-changes: no changes since the last migration (Phase 3 adds no migration).
- Work remains uncommitted. Next step: AUDIT PHASE 3 PRE-COMMIT GATE.
