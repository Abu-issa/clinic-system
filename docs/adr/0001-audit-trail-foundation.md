# ADR 0001 — Shared append-only audit trail foundation

Status: Accepted (implementation deferred to its own cross-cutting slice)
Date: 2026-09-16

## Context

The master specification requires a protected audit trail for clinical viewing, downloading,
editing, account linking, archive operations, and permission changes. The current
Medication/Prescription slice records attribution and lifecycle timestamps (created/last
modified/finalized/released/cancelled actors and times) on its aggregates, and per-request
persisted-grant revalidation covers authorization. These are provenance fields, not an audit
trail: they are mutable with their aggregate, they do not record reads or downloads, they do
not survive cancellations as independent records, and they cannot answer "who accessed what,
when, with what outcome" across modules.

No durable audit facility exists in the repository today (no audit tables, no audit service,
no write pipeline).

## Decision

1. A single shared, append-only `AuditEvent` foundation will be built as the immediate next
   cross-cutting slice — not a prescription-only audit table. Prescription download auditing
   will integrate into it when it exists; this phase deliberately does NOT create a partial
   prescription-only audit schema, because a fragment would not satisfy the master requirement
   and would immediately need migration.
2. Required fields at minimum:
   - Event ID (opaque, server-generated)
   - UTC timestamp (TimeProvider-sourced)
   - Actor staff/account ID
   - Action code (stable machine-readable string, e.g. `prescription.pdf.download`)
   - Resource type (e.g. `prescription`, `medication`, `staff-account`)
   - Opaque resource ID
   - Patient ID where the resource is patient-scoped (null otherwise)
   - Outcome (succeeded/failed)
   - Request/trace correlation ID (the existing Problem Details `traceId`)
   - Minimal safe metadata (bounded key/value pairs; see exclusions)
3. Hard exclusions from audit payloads (never recorded): passwords, tokens, OTPs, cookies,
   full request bodies, clinical note text, prescription instructions, diagnoses, medication
   clinical values beyond identifiers necessary to locate the event, and downloaded document
   bytes.
4. Write policy: audit writes for mutations should be transactionally consistent with the
   mutation where practical (same unit of work/transaction) so an audited event cannot claim a
   mutation that rolled back. Read/download audit writes are necessarily separate from the
   read path; their failure policy is explicit and must be chosen for that slice (fail-closed
   block the read, or fail-open with a durable local queue and alerting). Until that policy is
   implemented and reviewed, no download-audit guarantee is claimed.
5. Audit event reads/retention require their own authorization model and retention procedure;
   privileged direct database writes remain outside every guarantee, as elsewhere.

## Consequences

- Prescription PDF download events are NOT yet audited; the endpoint is a read-only
  presentation of an already-authorized record and the gap is explicitly accepted for this
  phase.
- The first audit slice must define the table(s), indexing, retention, and the API surface for
  querying events with staff-level authorization.
- Endpoints that will need integration (non-exhaustive): staff authentication operations
  (login, enrollment, revocation, grant changes), patient record reads/writes, visit
  finalization/amendment, medication catalog mutations, prescription lifecycle mutations,
  prescription PDF download, and future archive/linking operations.

## Compliance note

This record documents the decision; the requirement remains incomplete until the shared
foundation slice is implemented and tested. Do not represent attribution fields or this ADR as
a functioning audit trail.
