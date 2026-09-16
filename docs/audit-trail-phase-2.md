# Shared audit trail — Phase 2 mutation integration

Phase 2 continues the uncommitted handoff on foundation commit `8a95309`. It integrates the
successful operations below, not all application activity. No schema migration is required.
ClinicDb was not migrated or otherwise changed during this work. Tests use disposable
`ClinicTests_*` databases. Phase 3 has not started.

## Event catalog and coverage matrix

All events have `Outcome = Succeeded` and `TraceId = null`. GUID resource IDs use the opaque
`N` format. Staff resource IDs are the target Identity user ID. No metadata means an empty
dictionary. In the table, **staff** means the actual actor supplied by the trusted application
boundary; HTTP controllers derive it from validated staff claims, never a request-body actor.
**operator/null** means a trusted administrative caller may supply an actor; the existing
local CLI has no authenticated actor and leaves it null. Patient creation, administrative
update, and booking allow a null actor for direct non-HTTP callers; their staff controllers
supply the authenticated ID.

| Module | Existing operation | ActionCode | ResourceType | PatientId | Actor | Metadata keys | Coupled |
|---|---|---|---|---|---|---|---|
| Visit | FinalizeAsync | visit.finalize | visit | Visit.PatientId | staff | none | YES |
| Visit | AddAmendmentAsync | visit.amend | visit | Visit.PatientId | staff | none | YES |
| Patient | CreateAsync | patient.create | patient | Patient.Id | staff | none | YES |
| Patient | UpdateAsync | patient.admin.update | patient | Patient.Id | staff | none | YES |
| Patient | SaveProfileAsync | patient.clinical-profile.update | patient | Patient.Id | staff | entry-count | YES |
| Medication | CreateAsync | medication.create | medication | null | staff | none | YES |
| Medication | UpdateAsync | medication.update | medication | null | staff | none | YES |
| Medication | ActivateAsync | medication.activate | medication | null | staff | none | YES |
| Medication | DeactivateAsync | medication.deactivate | medication | null | staff | none | YES |
| Prescription | CreateDraftAsync, ordinary draft | prescription.create | prescription | Prescription.PatientId | staff | none | YES |
| Prescription | CreateDraftAsync, replacement | prescription.replace | prescription | Prescription.PatientId | staff | replaces-prescription | YES |
| Prescription | AddItemAsync | prescription.item.add | prescription | Prescription.PatientId | staff | item.count | YES |
| Prescription | UpdateItemAsync | prescription.item.update | prescription | Prescription.PatientId | staff | item.count | YES |
| Prescription | RemoveItemAsync | prescription.item.remove | prescription | Prescription.PatientId | staff | item.count | YES |
| Prescription | ReorderItemsAsync | prescription.item.reorder | prescription | Prescription.PatientId | staff | none | YES |
| Prescription | UpdateDraftNotesAsync | prescription.notes.update | prescription | Prescription.PatientId | staff | none | YES |
| Prescription | FinalizeAsync | prescription.finalize | prescription | Prescription.PatientId | staff | item.count | YES |
| Prescription | ReleaseAsync | prescription.release | prescription | Prescription.PatientId | staff | none | YES |
| Prescription | CancelAsync | prescription.cancel | prescription | Prescription.PatientId | staff | none | YES |
| Appointment | BookAsync | appointment.create | appointment | Appointment.PatientId | staff | appointment-type | YES |
| Appointment | RescheduleAsync | appointment.reschedule | appointment | Appointment.PatientId | staff | reschedule.id | YES |
| Appointment | CancelAsync | appointment.cancel | appointment | Appointment.PatientId | staff | none | YES |
| Staff | ProvisionFirstDoctorAsync | staff.provision | staff-account | null | null | scope-count | YES |
| Staff | ChangeAsync: disable | staff.disable | staff-account | null | operator/null | none | YES |
| Staff | ChangeAsync: revoke | staff.sessions-revoke | staff-account | null | operator/null | none | YES |
| Staff | ChangeAsync: reset-password | staff.password-reset | staff-account | null | operator/null | none | YES |
| Staff | ChangeAsync: reset-mfa | staff.mfa-reset | staff-account | null | operator/null | none | YES |
| Staff | ChangeAsync: set-grants | staff.grants.change | staff-account | null | operator/null | role-count, permission-count, patient-scope-count | YES |

Prescription replacement is one semantic event, attached to the new prescription with an
opaque link to the cancelled original. The new draft and the original's replacement pointer
are saved together. It emits neither an extra create nor an extra cancellation event; the
original must already have been cancelled. These are all nine state-changing public methods
on PrescriptionService, with two semantic variants of CreateDraftAsync.

Medication activation/deactivation retain existing idempotent business behavior: an accepted
call still saves the aggregate version and emits one command event, even if already in the
requested state. No `status-from`/`status-to` claim is made. A matching rerun of initial staff
provisioning changes nothing and emits no additional event.

## Transaction boundaries

The application uses `IAuditMutationWriter.Append` and a disposal scope; this interface has
no save method. The original `IAuditEventWriter` contract remains Append + SaveAsync for
standalone writes. AuditEventStore implements both on the same scoped ClinicDbContext as
the business repositories. No mutation calls standalone audit SaveAsync.

| Module | Actual atomic boundary | Independent failure evidence |
|---|---|---|
| Visit | Mutation + appended event in one store SaveAsync / EF SQL transaction | stale version, real SQL concurrency loser and retry, injected audit insert failure |
| Patient | Patient/profile aggregate + event in one store SaveAsync / EF SQL transaction | stale administrative update, injected audit insert failure |
| Medication | Catalog mutation + event in one store SaveAsync / EF SQL transaction | stale version, invalid input, injected audit insert failure |
| Prescription | One store save; finalization additionally retains its explicit medication-activity transaction and update locks | stale finalization, invalid finalization, notes-update audit insert failure |
| Appointment | Append and shared SaveChanges inside existing SqlBookingTransaction and doctor application lock | semantic success and overlap/stale/invalid-status rejection; existing concurrency regression tests; no dedicated appointment audit-insert failure injection |
| Staff | Existing explicit ClinicDbContext transaction encloses Identity saves, audit append, final db.SaveChangesAsync and commit | all six actions succeed with correct events; disable audit-insert failure rolls back previously saved Identity changes |

Staff Identity's user and role stores are registered with AddEntityFrameworkStores<ClinicDbContext>.
The scoped user manager, role manager and audit writer therefore use the same context and
SQL transaction. Identity performs multiple saves, but none commits independently of that
transaction. The repair adds the missing audit save before commit. This is a single-transaction
guarantee, not a claim of a single SaveChanges call for Identity administration. No listed
staff operation needed deferral because of a separate database/context.

Application construction must preserve these shared-context lifetimes. These are database
transaction guarantees, not distributed exactly-once delivery or retry idempotency guarantees.
Discard a failed request/CLI DI scope; no general recovery guarantee is introduced for reusing
Identity or appointment tracked business entities after provider or transaction-commit failures.

## Failure cleanup and duplicate review

The handoff's broad `or AuditEvent` filters in VisitStore, PatientRecordsStore,
MedicationCatalogStore and PrescriptionStore were unsafe: they also detached unrelated
pending events and already-saved history. Those additions were removed with precise edits;
the stores' pre-existing business-entity cleanup is unchanged.

Each audited operation now owns an audit scope. AuditEventStore records the actual event
instances appended in that scope, and disposal detaches only its still-Added instances.
It does not clear the context, detach earlier audit rows, or hide Modified/Deleted audit
violations. Tests for all four stores stage an unrelated event and retain saved audit history,
force the audited save to fail, then save again. Only the unrelated event can subsequently
persist; the rejected mutation and its event do not.

Controllers do not append events. Shared mutation helpers append only when requested; callers
do not append a second event. Replacement has one explicit event. Rejected validation,
stale versions, concurrency losers and rolled-back saves create no false Succeeded rows.
Successful retry tests prove one finalize event. A genuinely repeated successful command may
produce another event; no global uniqueness constraint or new idempotency protocol was added.

## Failure injection and SQL test scope

AuditMutationIntegrationTests spans AuditMutationIntegrationTests.cs and
AuditMutationReviewTests.cs. It uses isolated SQL fixtures and resource-scoped assertions,
not fixed-clock event ordering. Coverage includes all catalog actions, all prescription
semantic actions, both visit actions, all patient operations, the three appointment actions,
and all existing staff administrative actions.

The DbCommandInterceptor recognizes INSERT INTO [AuditEvents] on reader and non-query command
paths and throws a dedicated AuditInsertFailureException. Tests assert DbUpdateException and
that exact inner exception, then inspect a fresh context for unchanged aggregate state and
absence of the rejected event. Visit finalization remains Draft with no finalization time.
The four-store failure matrix also proves safe later saves and retention of unrelated audit
state. Staff disable has already executed Identity's update inside the explicit transaction
before the audit insert fails; the fresh context still sees the enabled user and original
security stamp. Thus that test proves rollback of SQL writes executed before the injected
failure, rather than merely observing an exception before any business command ran.

Failure injection is representative per module, not one independent fault-injection test for
every operation. In particular, appointment audit-insert failure, every individual staff
administrative failure, transaction-commit/network ambiguity, and every cancellation timing
are not independently fault-injected by the new tests.

## Metadata safety

This is the complete Phase 2 metadata-key catalog:

| Key | Values |
|---|---|
| entry-count | Total active clinical-profile entries, decimal count |
| item.count | Prescription item count after add/update/remove or at finalization |
| replaces-prescription | Original cancelled prescription ID, opaque GUID N |
| appointment-type | Validated enum name: Consultation or FollowUp |
| reschedule.id | Newly returned reschedule-history ID, opaque GUID N |
| scope-count | Initial provisioning doctor-scope count |
| role-count | Supplied approved-role count for set-grants |
| permission-count | Supplied permission count for set-grants |
| patient-scope-count | Supplied validated patient-scope count for set-grants |

Counts describe the validated command inputs where indicated; they are not a list of grants
or identities. Clinical content, names, contact information, medication text, dose/frequency/
duration/instructions, notes, amendment/cancellation reasons, RowVersion, passwords/hashes,
TOTP material, recovery codes, tokens, security stamps, cookies and raw grants/scope lists are
never emitted. The foundation's bounds and machine-token validation remain in force.
Optional amendment-ID metadata was removed: ordering by timestamp then GUID could select an
older amendment when timestamps tie. Optional medication transition metadata was removed
because idempotent calls need not transition from the assumed previous state.

## Deliberate limits and deferred work

- Failed/rejected attempts are not logged as Failed events in this phase.
- Login, failed login, MFA challenges, TOTP verification, recovery login, logout and ordinary
  session validation remain deferred. Explicit administrative reset/revoke mutations above
  are integrated; ordinary access/security events are not.
- Reads, availability queries, prescription PDF downloads, audit query API/UI and export
  remain deferred. Printing behavior is unchanged.
- Visit creation, draft edits and vitals remain outside this phase's finalize/amend scope.
  Schedules and other unlisted mutations are not claimed as audited.
- Appointment domain status methods exist, but there is no separate existing application
  status-transition service to integrate. No such service was invented. Staff has no enable
  command in its existing administrative switch.
- Retention, authorized audit querying/export and read/download failure policy remain unresolved.
- No Phase 3, new feature module, migration application, commit or push was performed.

## Verification (2026-09-16)

- Build: 0 warnings, 0 errors.
- Targeted audit mutation tests: 26 passed, 0 failed, 0 skipped.
- Full suite: 170 unit + 328 integration = 498 passed, 0 failed, 0 skipped.
- EF has-pending-model-changes: no changes since the last migration, AddAuditFoundation.
- git diff --check: clean. Phase 1 migrations and snapshot unchanged.
- SQL execution required running outside the restricted sandbox because its Windows SQL
  encryption support failed during fixture initialization. The successful runs used the
  unchanged isolated ClinicTests_* fixture; no production connection or security setting was
  changed to bypass that environment restriction.
- Work remains uncommitted. Next step: AUDIT PHASE 2 PRE-COMMIT HARDENING GATE.
