# Medication Catalog and Prescriptions — Phase 1

This phase provides domain behavior, application services and relational persistence only.
There are no Medication or Prescription HTTP endpoints, public contracts, new permissions or
grant changes yet. The API composition root registers `MedicationCatalogService`,
`IMedicationCatalogStore`, `PrescriptionService` and `IPrescriptionStore`; it exposes neither
directly. Application results are plain enums plus DTO projections. No HTTP status mapping
(no claim of 409 mapping), no endpoint authorization, no controllers, no PDF generation and no
UI exist in this phase; those belong to Phase 2.

## Medication catalog

`Medication` is an aggregate root with SQL rowversion optimistic concurrency. It records
multilingual generic/brand names (at least one generic name required), category, dosage form,
route and active status. New entries start active. Deactivation/activation are idempotent and
record actor and UTC time from TimeProvider.

**Strength and unit representation (documented contract).** `Strength` and `Unit` are separate
compact tokens and never overlap. Strength carries only the value: `"500"`, `"12.5"`. Compound
strengths separate components with `/`: `"500/125"` for 500 mg + 125 mg per unit, with `Unit`
naming the shared unit. Ratio strengths over a volume use a ratio unit: Strength `"5"` +
Unit `"mg/ml"`. Embedded units (`Strength = "500 mg"` alongside `Unit = "mg"`) are rejected by
the domain, and SQL check constraints (`CK_Medications_StrengthToken`,
`CK_Medications_UnitToken`) reject any stored value containing whitespace, covering direct
privileged writes. No clinical defaults, dose calculations, unit conversion or normalization
are performed anywhere in this phase.

Catalog identity is the generic name (En or Ar) with strength, unit, form and route. The
application rejects duplicate creation (`DuplicateMedication`), counting inactive entries too,
so a withdrawn product cannot be re-created as a fresh active duplicate. Filtered unique
indexes back this against concurrent creates; the unique-index violation is translated to the
same controlled result. Brand names and category do not distinguish products.

## Prescription aggregate

`Prescription` belongs to a Visit and derives PatientId and DoctorId strictly from it (the
application validates the visit reference; foreign keys enforce existence). Lifecycle:
Draft → Finalized → Released → Cancelled, with SQL rowversion on the root. A SQL check
constraint (`CK_Prescriptions_Lifecycle`) keeps the audit columns consistent with status, and
`CK_Prescriptions_ReplacedByOnlyWhenCancelled` ties the replacement back-reference to cancelled
rows. Statuses are compared as ints by the constraints; CancellationReason is required and
bounded (1000 chars) from Finalized/Released; cancelling is otherwise content-preserving.

**Incomplete drafts.** Dose, frequency and duration on `PrescriptionItem` are nullable bounded
text (200 chars each; instructions 1000). They may be absent while the prescription is a Draft;
whitespace-only input normalizes to null, and oversized values are rejected in every draft
state. Structural completeness (all three present) is enforced by the aggregate at
finalization; the service additionally refuses finalization with any incomplete item. There are
no dose calculations, frequency parsing or clinical defaults — values are recorded as written.

**Aggregate integrity.** `PrescriptionItem` has no public mutators and no public constructor;
creation, updates, reordering and removal happen only through `Prescription` methods, which are
Draft-guarded. Every item mutation advances the root's LastModified metadata, and every write
goes through the store, which sets EF's original rowversion to the caller's expected version and
forces a root update even for child-only changes at a fixed clock instant — so item edits
participate in root concurrency. Explicit draft item removal deletes the severed row in the same
save (EF client-side orphan deletion); the database foreign key is NO ACTION and no orphan row
remains (verified by tests).

**Items and snapshots.** Items copy the catalog snapshot (names, strength, unit, form, route) at
insertion; catalog edits never leak into existing snapshots, and only an explicit reselection in
a draft refreshes it. Finalized items are immutable. No medication-prescription numeric linking
beyond the reference and snapshot exists in this phase.

## Replacement concurrency

Creating a replacement for a cancelled prescription writes both directions in one save: the new
prescription's `ReplacesPrescriptionId` and the original's `ReplacedByPrescriptionId`. The
authoritative direction is the forward link: a filtered unique index on `ReplacesPrescriptionId`
enforces at most one replacement per original for direct writers too, since the original's
rowversion only serializes cooperative service calls. A complementary filtered unique index on
`ReplacedByPrescriptionId` enforces one original per replacement, keeping the two stored
directions 1:1; a violation maps to the controlled `ReplacementMismatch` result without
exposing stored identifiers. The reverse link is a guarded, domain-written cache of the
forward relationship — every write path sets both directions together under the original's
optimistic concurrency, domain guards forbid overwriting an existing replacement link, and
tests assert both directions agree; direct privileged writes outside these paths remain
outside the guarantee as elsewhere in this schema. The caller must also supply the original's
current 8-byte rowversion (`ExpectedOriginalRowVersion`); a stale or missing version fails with
`PrescriptionChanged`/`InvalidRowVersion` before any write. Concurrent replacement attempts
are serialized by that optimistic update: exactly one succeeds, the loser receives
`PrescriptionChanged` or `ReplacementMismatch` depending on which guard fires first, and the
unique indexes backstop multiple replacements for one original even under direct writes.
Domain-level guards keep the two directions consistent: a cancelled prescription can be linked
to exactly one replacement, a different replacement cannot overwrite an existing link, and
mismatches (status, visit, patient, doctor) fail with `ReplacementMismatch` pre-write.

## Catalog activity at finalization

Finalization eligibility (every referenced medication still active) and the finalization save
share one transaction. `IPrescriptionStore.BeginMedicationActivityScopeAsync` opens it, and the
catalog read takes `UPDLOCK, ROWLOCK` on the referenced medication rows: a deactivation
committed before the read is observed (finalization fails with `InactiveMedication` and the
draft is preserved), while a deactivation that has not committed waits until the finalization
commits and then applies. A finalized prescription therefore never references a medication that
was deactivated before the finalization's commit. The scope commits with the save or rolls back
on disposal. Tests exercise both orderings with real concurrent transactions; no timers or
fake infrastructure are involved. Note the residual Phase 2 consideration: a deactivation that
commits *after* a finalization does not retroactively invalidate the finalized prescription —
that is the documented semantic, not a gap.

## Application and persistence

Results use `MedicationCatalogError`/`PrescriptionError` plus explicit DTO projections; tracked
entities never appear in responses. Existing-object mutations require the 8-byte expected
rowversion; actual SQL conflicts surface as `PrescriptionChanged`/`MedicationChanged`. Rejected
commands detach their pending aggregate state so a later save cannot flush partial mutations
(store-level `DiscardChanges` on every save failure). Lists are bounded (take 1–100, default
50) and projections omit clinical detail. Actor is a separate trusted-caller argument; services
are internal components, not authorization boundaries. Byte arrays retain the project's Base64
JSON convention for Phase 2.

## Migration and safety

Generated by the installed EF toolchain:
`20260915201721_AddMedicationCatalogAndPrescriptions` (migration, designer and snapshot).
Up creates only Medications, Prescriptions and PrescriptionItems, their indexes (including the
two filtered unique catalog indexes and the two filtered unique replacement-link indexes), primary/foreign
keys, both rowversions, and the strength/unit/lifecycle/replacement check constraints. All new
foreign keys are NO ACTION (Restrict) — including prescription items; no cascade deletes and no
changes to existing tables, rows, IDs, MRNs or rowversions. No data is fabricated or seeded.
Down drops the three new tables and destroys prescription history: do not downgrade a populated
deployment without an approved backup/recovery plan.

The migration has NOT been applied to ClinicDb. Tests use isolated ClinicTests databases only.

## Before Phase 2 — authorization contract

Phase 2 must define, before any HTTP exposure, and must not be assumed from this phase:

- Role/permission/policy mapping for catalog administration (create/update/deactivate —
  expected: an administrative permission such as `medications.admin.write`, Doctor or
  Pharmacist-adjacent roles as decided) versus catalog search and prescription authoring.
- Prescription authorship rule: who may create/finalize/release/cancel — expected to follow the
  Visits model: the prescribing doctor verified against `StaffUser.AssociatedDoctorId` from the
  live database per request (cookie claims alone are insufficient), with reception and
  assistants excluded from clinical prescription decisions.
- Patient-scope enforcement: exact persisted `patient_record_id` claim matching on every
  patient-scoped route, continuing MFA (`amr=mfa`), per-request grant revalidation, CSRF,
  no-store and generic sanitized Problem Details conventions.
- Status-code mapping decided once, centrally (e.g. `PrescriptionChanged` → 409,
  `InvalidLifecycle`/`ReplacementMismatch` → 409 or 422 as a single documented choice) —
  nothing in Phase 1 presupposes a status code.
- Whether releasing to patients, printing/PDF, pharmacy workflows and item-level dispensed
  quantities are in scope, and the retention/audit requirements for cancelled prescriptions.

Phase 1 is complete; there is no technical blocker. Production prerequisites (durable auditing,
retention/recovery, restricted/encrypted storage, payload-safe logging) remain as documented in
prior phase notes.
