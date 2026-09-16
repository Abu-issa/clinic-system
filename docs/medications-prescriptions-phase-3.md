# Medication Catalog and Prescriptions — Phase 3

Phase 3 completes the slice with printable bilingual prescription PDFs, a protected staff PDF
endpoint, the release/printing semantics review, the durable audit decision, and a final
security review. No patient authentication, patient-facing endpoints, dispensing, inventory,
notifications, or UI exist. No migration was needed; the schema is unchanged and the Phase 1
migration remains the only one (still NOT applied to ClinicDb).

## PDF library, fonts, and licenses (verified from official sources)

- **QuestPDF 2026.9.0**, pinned via central package management
  (`backend/Directory.Packages.props`). License verified from questpdf.com/pricing:
  source-available commercial software — **not MIT**. The Community tier is free only for
  individuals, open-source, charities, academia, and organizations with **annual gross revenue
  under USD 1,000,000**; government/public-sector bodies and publicly traded companies are not
  eligible at any revenue. **Production eligibility has NOT been verified for this clinic**;
  before any production deployment the clinic must confirm it meets the Community conditions or
  purchase Professional/Enterprise. No license key, watermark, or activation is involved
  (good-faith self-certification).
- Fonts bundled reproducibly as embedded resources in `Clinic.Infrastructure` (no reliance on
  Windows-installed fonts; `UseSystemFonts = false`): **Noto Sans Arabic** and **Noto Sans**,
  Regular weights, from the official notofonts project. Both are licensed under the **SIL Open
  Font License 1.1**, which permits embedding in documents; the required copyright/license
  text ships beside the font files (`Fonts/LICENSE-OFL.txt`) and is embedded into the assembly.
- Arabic shaping and RTL layout are handled by QuestPDF (HarfBuzz-based) — no custom shaping.

## Rendering flow (dependencies point inward)

- Application (`PrescriptionPrint.cs`): `PrintLanguage`, `PrintClinicDetails` (bound from
  validated configuration), the print projection records, `IPrescriptionPrintStore`,
  `IPrescriptionPdfRenderer`, and `PrescriptionPrintService`. Application never references
  ASP.NET, Identity, EF, or QuestPDF. The view type cannot carry what it does not declare —
  tests pin the exact allowed property set.
- Infrastructure (`Printing/`): `PrescriptionPrintStore` assembles the projection (prescription
  items ordered by DisplayOrder, plus CURRENT Patient/Doctor display identity),
  `QuestPdfPrescriptionRenderer` renders. No EF entity crosses the boundary.
- API: authorization and result mapping only; returns `application/pdf` bytes.

## PDF endpoint

`GET /api/staff/patients/{patientId}/prescriptions/{prescriptionId}/pdf?language=ar|en`

- Authorization identical to prescription details: full MFA staff session, Doctor role,
  persisted `prescriptions.read`, exact persisted `patient_record_id` scope. Read-only GET:
  no CSRF, no state transition. Scheduling claims grant nothing; DoctorAssistant and
  Receptionist are excluded (same as prescription reading).
- Unsupported or missing language → 400 `invalid_input`. Draft/Cancelled → 409
  `not_printable`. Wrong-patient routes → hidden 404 `prescription_not_found`, exactly like
  details. Revoked grants → 401 via persisted revalidation.
- Response: `application/pdf`, `Cache-Control: no-store`, `Content-Disposition: attachment;
  filename="prescription-{id}-{language}.pdf"` — filename contains only the route prescription
  id and the whitelisted language token (no patient name/MRN, no header injection surface).
- Document size is bounded by domain text limits plus a 200-item service cap; rendering
  failures surface as sanitized errors (`PrescriptionRenderingException` → generic handler;
  no clinical values attached).

## PDF content and deliberate exclusions

Included: clinic name/address/phone (validated configuration — synthetic placeholders ship in
appsettings.json and MUST be replaced per deployment), prescription identifier, prescription
(finalization) date, patient name, MRN, date of birth when stored, prescribing doctor display
name, items in DisplayOrder with snapshot generic/brand names, snapshot strength/unit/form/
route, dose, frequency, duration, instructions, page numbering ("Page X / Y") on every page,
and a visible footnote whenever a medication name lacked the requested translation and the
stored alternate name was used verbatim (no translation is ever invented).

Excluded by design: internal Visit notes, diagnosis, ClinicianNotes/InternalNotes, cancellation
reason, rowversions, Visit/Appointment identifiers, staff account IDs, claims/scopes, and any
security metadata. The view type enforces this structurally.

Language behavior: Arabic uses RTL layout with Noto Sans Arabic (Noto Sans as ordered font
fallback); English uses LTR. Numeric values, units and dose direction are preserved in
bidirectional text by giving stored values a strong LTR embedding (U+200E) in Arabic output —
without this, QuestPDF displayed "500 mg" as "mg 500" (verified visually and fixed).
Item clinical content always comes from the finalized item snapshots; current catalog values
are never read for rendering (verified: catalog edits do not change rendered bytes).

## Identity data currency limitation

Patient name/MRN/DOB and doctor display name are read from CURRENT records at print time, not
snapshotted at finalization. Re-printing after an identity correction reflects the corrected
identity, so the exact historical document is NOT guaranteed byte-reproducible if display
identity changed since finalization (item content and layout remain deterministic). This was
chosen as the smallest design consistent with reliable prescriptions; an immutable issue
snapshot would require a new table plus a finalization-time copy and was deliberately not
introduced in this phase.

## Release and printing semantics

- Finalization makes the prescription clinically immutable and printable; Release remains a
  separate stored transition.
- "Released" means eligible for a future patient-facing channel; that channel does not exist.
- Generating or downloading a PDF changes nothing: no status change, no RowVersion advance
  (verified by test), no Appointment/Visit state change.
- Cancelling preserves release history but new printable PDFs are refused (409).
- Replacement continues to require the existing cancellation/replacement workflow.
- Printing never finalizes, releases, cancels, or replaces anything. If the clinic wants
  "issue PDF" to be a state-changing, recorded operation, that is an explicit product decision
  to be added on top of this read-only endpoint — not silently changed here.

## Audit decision

No durable audit facility existed. A partial prescription-only audit schema was deliberately
NOT built. See [ADR 0001](adr/0001-audit-trail-foundation.md): a shared append-only AuditEvent
foundation is the next cross-cutting slice, with required fields, hard payload exclusions,
transactional-consistency policy for mutations, and an explicit (to-be-chosen) failure policy
for read/download auditing. PDF download events are not audited yet; this gap is accepted and
documented, not hidden.

## Security review results (complete slice)

Re-verified: cross-patient access (hidden 404 vs scope 403), cross-doctor mutation (persisted
AssociatedDoctorId), scheduling-scope privilege escalation (rejected), stale persisted grants
(401 via claim-type-based revalidation), DTO overposting (bodies carry only notes/instructions
identifiers/rowversions), malformed rowversions (400), finalized/cancelled immutability,
snapshot preservation, replacement-link consistency, deactivation/finalization race (UPDLOCK
window), PDF authorization before any resource work, no markup/HTML interpretation (all text
drawn literally), font fallback (ordered bundled fonts), log leakage (no PDF content logged),
no-store on all responses including PDFs, filename/header injection (whitelist-derived), and
generation resource use (read-only GET, bounded items, no body). One defect was found and
fixed during review: missing instructions column in the item table, and the RTL numeric
reversal described above.

## Remaining limitations

- Audit trail not implemented (ADR 0001 is the decision; downloads unaudited until then).
- QuestPDF Community production eligibility unverified for the clinic.
- Clinic display configuration ships synthetic placeholders; real values must be configured
  with startup validation already in place.
- Identity display data is current-record based (historical reproduction caveat above).
- No patient-facing release channel, dispensing, or inventory; PDF is staff-only.
