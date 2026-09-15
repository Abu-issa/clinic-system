# Visits and vital measurements — Phase 1

This phase provides domain behavior, application services and relational persistence only.
There are no Visit HTTP endpoints, public contracts, new permissions or grant changes yet.
The API composition root registers `VisitService` and `IVisitStore`; it exposes neither directly.

## Aggregate and lifecycle

`Visit` identifies a Patient and Doctor, an optional Appointment, and an explicit UTC encounter
time. Clinical fields are nullable: ChiefComplaint, Symptoms, Diagnosis, ClinicianNotes,
InternalNotes, PatientSummary and SuggestedFollowUpAtUtc. No clinical values are inferred.
InternalNotes and PatientSummary are separate fields in persistence and staff detail projections.
Creation and modification record UTC time and staff ID. Service-generated times use TimeProvider.

A new Visit is Draft. `UpdateDraft` replaces its six clinical text fields and follow-up time as
one operation; null clears a draft field. Non-null whitespace is rejected; text is limited to
8000 characters. Validate the entire content object before modifying tracked state. This is an
internal application contract: Phase 2 must explicitly define field-presence semantics rather
than binding an incomplete JSON body to nullable defaults.

Finalization records the actor and time. After finalization, normal content edits, vital additions
and repeat finalization are rejected. There is no unfinalize or appointment status transition.
Phase 1 does not impose unapproved mandatory diagnosis/complaint rules at finalization.

`VisitAmendment` requires a non-empty reason and content, only on a finalized Visit. Rows retain
ID, VisitId, creation time and actor; no normal edit/delete methods exist. Adding an amendment
advances aggregate LastModified metadata and RowVersion, while leaving original clinical content
and finalization metadata unchanged. Details order amendments by creation time, then ID for ties.
This attribution/history is not a complete audit trail or protection against privileged SQL writes.

## Vitals

`VitalReading` is a closed family of typed inputs. The caller cannot choose a unit. Persisted
`VitalMeasurement` rows contain type, fixed unit, type-specific nullable numeric columns,
measurement time, creation time and actor. The SQL shape constraint requires exactly the correct
columns and unit, including explicit non-null checks. Rows are never replaced by another reading.
There are no cached Patient vitals or JSON blobs.

| Input | Columns | Unit |
| --- | --- | --- |
| BloodPressure | SystolicMmHg, DiastolicMmHg (integers) | mmHg |
| HeartRate | HeartRateBpm (integer) | bpm |
| Temperature | TemperatureCelsius | Celsius |
| OxygenSaturation | OxygenSaturationPercent | percent |
| Weight | WeightKg | kg |
| Height | HeightCm | cm |

Decimal fields use decimal(10,3); inputs exceeding its range or precision are rejected rather
than rounded silently. Physical unsigned readings reject negatives; percentage is 0–100.
Negative Celsius is allowed. No normal ranges, systolic/diastolic ordering rule, inferred clinical
defaults or interpretation is added. Occurred/measurement/follow-up times are normalized to UTC;
no speculative clinical date window is imposed. Vitals are ordered by measured time, creation time,
then ID. Measurements remain possible at different recorded times during the Draft lifecycle.

## Application and concurrency

`VisitService` supports CreateAsync, GetAsync, ListAsync, UpdateAsync, AddVitalAsync,
FinalizeAsync and AddAmendmentAsync. Results use VisitError plus explicit DTO projections;
tracked entities do not appear in responses. List results omit clinical text and are bounded
to 100 rows, default 50, ordered by encounter time and ID. Existing-visit operations require both
PatientId and VisitId; lookups bind both. The service is an internal trusted component, not an
authorization boundary. Actor is a separate argument to be supplied by the trusted staff principal.

All five mutation categories (draft creation, editing, vitals, finalization, amendments) save
through the existing ClinicDbContext unit of work. Existing-visit mutations require an 8-byte
expected SQL rowversion. The service checks the loaded version, then the store sets EF's original
version and forces a root update even when a fixed clock produces identical modification times.
Root and children save atomically; actual SQL concurrency conflicts become VisitChanged. Invalid
commands/save failures discard tracked Visit aggregates so a later SaveChanges cannot retry rejected
mutations. Successful details contain the committed rowversion, copied into the DTO. Byte arrays
retain the project's standard Base64 JSON convention for Phase 2.

Creation checks Patient and Doctor existence. A linked Appointment must exist and match both IDs.
No name matching, account creation, automatic completion or patient/doctor reassignment occurs.
There is no new unique AppointmentId constraint: Phase 1 does not decide one encounter per booking.
Inactive-doctor historical encounters are not forbidden by this foundation; Phase 2 must apply
the approved clinician/action authorization rules. Foreign keys enforce existence; matching the
appointment's immutable PatientId/DoctorId is checked by the application, not a new alternate key
on the existing Appointment table. Privileged direct database writes remain outside this guarantee.

## Migration and safety

Generated by the installed EF toolchain:
`20260915161502_AddVisitsAndVitalMeasurements` (migration, designer and snapshot).
Up creates only Visits, VisitAmendments and VitalMeasurements, five indexes, primary/foreign keys,
Visit rowversion and lifecycle/vital check constraints. All new foreign keys use Restrict
(SQL Server NO ACTION), including children; no cascade deletes are introduced.

Patient, Appointment, medical-record and Identity tables are not altered or recreated. Existing
IDs, MRNs (including NULL), Appointment.PatientId, rowversions and clinical rows are untouched.
No data is fabricated or seeded. Upgrade testing starts at the previous medical-record migration
in a unique ClinicTests database, inserts synthetic historical data and verifies it after upgrade.
Down drops all three new tables and destroys their clinical history: do not downgrade a populated
deployment without an approved backup/recovery and retention plan.

The migration has NOT been applied to ClinicDb. Tests use isolated ClinicTests databases only.

## Before Phase 2

Define explicit role/permission/action and persisted patient/doctor resource-scope requirements,
including who may record vitals versus finalize/amend. Do not equate staff login with clinical
authority. Continue the existing MFA, per-request persisted grant validation, CSRF, no-store and
generic Problem Details conventions. Map VisitChanged to concurrency conflict without clinical
payloads in errors or logs. Do not return this staff detail DTO through any future patient endpoint.

Clarify any required finalization fields, one-visit-per-appointment rule, date window constraints,
vital correction workflow and attribution/retention requirements before implementing such rules.
There is no technical Phase 1 blocker; HTTP authorization and privacy verification remain Phase 2.
Production still needs durable auditing, retention/recovery procedures, encrypted/restricted
database storage and backups, and payload-safe logging/APM. This phase adds no payload logging.
