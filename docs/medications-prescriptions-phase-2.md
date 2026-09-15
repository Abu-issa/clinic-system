# Medication Catalog and Prescriptions — Phase 2

Phase 2 exposes the Phase 1 domain and application services as protected staff HTTP endpoints.
No PDF generation, patient-facing endpoints, notifications, dispensing, pharmacy inventory or
audit framework exist yet. No migration was needed or added; the Phase 1 migration
(`20260915201721_AddMedicationCatalogAndPrescriptions`) remains the only one and has NOT been
applied to ClinicDb.

## Routes

Medication catalog (clinic-wide, no patient scope):

- `GET    /api/staff/medications?query={q}&activeOnly={bool}&skip={n}&take={n}` — search (take 1–100, default 50)
- `GET    /api/staff/medications/{medicationId}`
- `POST   /api/staff/medications`
- `PUT    /api/staff/medications/{medicationId}`
- `POST   /api/staff/medications/{medicationId}/deactivate`
- `POST   /api/staff/medications/{medicationId}/activate`

Prescriptions (patient-scoped routes; PatientId/DoctorId/Visit ownership/lifecycle are always
server-derived, never accepted from bodies):

- `POST   /api/staff/patients/{patientId}/visits/{visitId}/prescriptions` — create Draft from the visit
- `GET    /api/staff/patients/{patientId}/visits/{visitId}/prescriptions`
- `GET    /api/staff/patients/{patientId}/prescriptions?skip&take`
- `GET    /api/staff/patients/{patientId}/prescriptions/{prescriptionId}`
- `PUT    /api/staff/patients/{patientId}/prescriptions/{prescriptionId}/notes`
- `POST   /api/staff/patients/{patientId}/prescriptions/{prescriptionId}/items`
- `PUT    /api/staff/patients/{patientId}/prescriptions/{prescriptionId}/items/{itemId}`
- `DELETE /api/staff/patients/{patientId}/prescriptions/{prescriptionId}/items/{itemId}`
- `PUT    /api/staff/patients/{patientId}/prescriptions/{prescriptionId}/items/order`
- `POST   /api/staff/patients/{patientId}/prescriptions/{prescriptionId}/finalize`
- `POST   /api/staff/patients/{patientId}/prescriptions/{prescriptionId}/release`
- `POST   /api/staff/patients/{patientId}/prescriptions/{prescriptionId}/cancel`
- `POST   /api/staff/patients/{patientId}/prescriptions/{prescriptionId}/replacement`

The replacement endpoint operates on the cancelled original (the route prescription), requires
its `expectedOriginalRowVersion`, creates exactly one replacement Draft through the Phase 1
service, and returns the new prescription with its persisted RowVersion.

## Role / permission matrix

| Action | Roles | Permission | Patient scope | Doctor authority | MFA + CSRF |
| --- | --- | --- | --- | --- | --- |
| Medication search/details | Doctor, DoctorAssistant | `medications.read` | not required | no | MFA; CSRF n/a (GET) |
| Medication create/update/deactivate/activate | Doctor | `medications.manage` | not required | no | yes |
| Prescription read/list | Doctor | `prescriptions.read` | exact route patient | no | MFA |
| Draft create / notes / items / reorder / replacement | Doctor | `prescriptions.write` | exact route patient | yes | yes |
| Finalize | Doctor | `prescriptions.finalize` | exact route patient | yes | yes |
| Release | Doctor | `prescriptions.release` | exact route patient | yes | yes |
| Cancel | Doctor | `prescriptions.cancel` | exact route patient | yes | yes |

DoctorAssistant and Receptionist are excluded from prescription content in this initial slice.
Receptionist is excluded from the catalog API entirely. No permission is granted automatically
by provisioning; grants remain explicit administrative operations (`set-grants`). Catalog
authority is clinic-wide: scheduling scopes (`appointment_doctor_id`, `schedule_doctor_id`)
and patient scopes never grant catalog management, and scheduling scopes never grant
prescription authority.

## Doctor authority rule

Every prescription mutation verifies, per request, that the persisted
`StaffUser.AssociatedDoctorId` (loaded from the database via UserManager, never trusted from
cookie claims) equals the prescribing doctor stored on the target Visit (creation) or
Prescription (all other mutations). Reading is not doctor-gated. There is no cross-doctor
prescribing delegation in this phase. Changing the persisted association takes effect on the
next request even for an existing cookie, because the cookie carries no association claim.

## Patient scope rule

All prescription routes require an exact persisted `patient_record_id` claim matching the
route patient (the same mechanism as Visits). Per-request persisted-grant revalidation is
claim-type based, not prefix based: every `permission` claim, every `patient_record_id`,
`appointment_doctor_id` and `schedule_doctor_id` scope claim, and every role claim in a full
session is checked against current persisted state on every request, because those claims can
only have been issued from persisted state at login. Grant or role removal is therefore
rejected with 401 even on an unchanged cookie for every current and future permission, while
additions still require a new login (policies read cookie claims only). Route/resource
mismatches return 404 so resource existence is not revealed, consistent with the existing
clinical API; callers without the patient scope receive 403.

## Lifecycle behavior over HTTP (Phase 1 rules preserved)

Drafts may contain incomplete items (null dose/frequency/duration); finalization requires at
least one complete item and every referenced medication active. Finalization and Visit
finalization are independent; finalization never completes an Appointment. Finalized content
and notes are immutable. Release is a separate step from finalization; cancellation (reason
required) preserves content and release history; releasing a cancelled prescription is refused.
Exactly one replacement can exist per cancelled original (rowversion + unique-index
enforcement). Item snapshots do not change when the catalog changes; adding or reselecting an
item requires an active medication.

## Concurrency contract

RowVersion is a SQL Server rowversion exposed as Base64 in JSON (the project's standard
`byte[]` convention). Every mutation of an existing object requires `expectedRowVersion` in the
body; replacement additionally requires `expectedOriginalRowVersion`; successful responses
return the newly persisted version. Stale versions produce 409, never a partial write. Child
item changes advance the root RowVersion, and item mutations return the updated root version.

## Problem Details codes

Errors use Problem Details with `code` and `traceId` extensions and no clinical payload,
identifier leakage or exception text.

| Status | Codes |
| --- | --- |
| 400 | `invalid_input` (malformed DTO, missing reason, invalid paging), `invalid_row_version` (missing/malformed/wrong-length Base64), `invalid_csrf_token` |
| 401 | anonymous, restricted non-MFA intermediate session, revoked/changed persisted grants, disabled account |
| 403 | role/permission/patient-scope denial, failed doctor authority |
| 404 | `medication_not_found`, `visit_not_found`, `prescription_not_found`, `prescription_item_not_found` (including route/resource mismatches that hide existence) |
| 409 | `medication_changed`, `duplicate_medication`, `prescription_changed`, `inactive_medication`, `invalid_lifecycle`, `replacement_mismatch`, `incomplete_prescription` (finalize-specific incomplete draft) |

There is no 422 usage; conflicts are 409 as in the existing API.

## Session security

All mutations require the full MFA staff cookie (`ClinicStaff`, `amr=mfa`) and the existing
`X-CSRF-TOKEN` antiforgery validation; restricted enrollment/password-only intermediate
sessions receive 401. GET endpoints mutate nothing. All medication and prescription responses
are `Cache-Control: no-store` (the no-store path filter now covers `/api/staff/medications`).

## Remaining limitations (future work)

- Bilingual prescription PDF and any print layout are not implemented.
- Release currently means the status transition; there is no patient-facing release channel or
  printing verification.
- Attribution (actor/time fields) is recorded in persistence but deliberately omitted from
  responses beyond lifecycle timestamps; a durable audit integration decision is still open.
- No dispensing, quantities, pharmacy inventory, notifications or UI exist.
