# Audit trail — Phase 1 (shared foundation)

Phase 1 of the audit trail slice implements ONLY the reusable append-only foundation defined
by [ADR 0001](adr/0001-audit-trail-foundation.md). No existing feature writes audit events yet;
endpoint integration is Phase 2, and read/download auditing with its failure policy is Phase 3.
The system does NOT yet have complete audit coverage — only the foundation exists.

## Implemented schema (`AuditEvents`)

| Column | Type | Notes |
| --- | --- | --- |
| Id | uniqueidentifier | server-generated, opaque, primary key |
| OccurredAtUtc | datetimeoffset | TimeProvider-sourced, normalized to UTC; callers never supply event time |
| ActorStaffId | nvarchar(450), null | ASP.NET Identity staff/account ID; null only for legitimate system events |
| ActionCode | nvarchar(100), required | stable machine-readable action, e.g. `prescription.finalize` |
| ResourceType | nvarchar(100), required | stable machine-readable kind, e.g. `prescription`, `staff-account` |
| ResourceId | nvarchar(256), required | opaque string representation; future resources need not be GUIDs |
| PatientId | uniqueidentifier, null | present when the resource is patient-scoped and known |
| Outcome | int | `AuditOutcome.Succeeded` / `AuditOutcome.Failed` |
| TraceId | nvarchar(128), null | HTTP Problem Details / request correlation ID |
| MetadataJson | nvarchar(4000) | bounded safe key/value metadata as JSON; empty object when none |

Indexes (recent-event-friendly, ascending columns ordered for range scans): `OccurredAtUtc`;
`ActorStaffId + OccurredAtUtc`; `ResourceType + ResourceId + OccurredAtUtc`;
`PatientId + OccurredAtUtc`; `ActionCode + OccurredAtUtc`.

**No foreign keys** to StaffUser, Patient, Visits, Prescriptions, Medications, or any clinical
aggregate: audit history remains readable even if source records are later removed, and no
cascade can ever delete audit rows.

## Code validation

ActionCode and ResourceType must be stable machine-readable codes: lowercase ASCII letters and
digits with dot/hyphen separators (`^[a-z0-9][a-z0-9.-]{1,98}[a-z0-9]$`), max 100 characters.
Blank, whitespace, uppercase, spaces, emojis, control characters, and clinical prose are
rejected. Metadata keys follow the same style (`^[a-z0-9][a-z0-9._-]{0,63}$`, max 64).

## Metadata design (narrow API, not object serialization)

- Max 20 entries; keys ≤ 64 chars; values ≤ 256 chars; total raw key+value text ≤ 2000 chars
  (escaped JSON never exceeds the 4000-character column).
- Keys are lowercase machine-readable tokens; **values are machine-safe tokens only**
  (letters, digits, dot, underscore, colon, slash, plus, hyphen — e.g. reason codes,
  identifiers, counts, flags, dates). Values are trimmed before validation. Free-form prose,
  spaces, and non-ASCII text are **rejected by validation**, so clinical text cannot be stored
  under an innocuous key such as `note`.
- Values are strings only. `Dictionary<string, object>`, `object`, `dynamic`, and DTO
  serialization are not expressible through the contract.
- Duplicate keys are rejected; metadata is exposed as a true read-only dictionary after
  creation (the constructor copies caller-supplied entries, so later caller-side mutation has
  no effect) and the entity has no public mutators at all.
- Credential/secret-smelling keys are rejected outright (`password`, `token`, `secret`,
  `cookie`, `authorization`, `api-key`, `connection-string`, `credential`, `otp`, `totp`,
  `recovery`). This is defense-in-depth only, and it cannot detect every sensitive key name
  (e.g. `auth`, `payload`, `details`); the machine-safe value model is what prevents prose.

## Hard payload exclusions — layered guarantees

- **Structurally impossible:** anything that is not a bounded string-to-string pair; more than
  20 entries; over-length keys/values/totals; non-string objects; unbounded clinical text
  (it cannot fit the token format or the size bounds).
- **Validation-rejected:** free-form prose and non-ASCII text (machine-safe value model),
  control characters, credential/secret-smelling key names, blank or duplicate entries.
- **Integration responsibility:** choosing correct keys and machine-safe values, and not
  encoding sensitive or clinical content into allowed characters. Phase 2 event emitters must
  review every metadata key/value they introduce; the API makes accidental serialization of
  sensitive objects difficult, but it cannot read intent.

## Append-only guarantees

- The Application contract (`IAuditEventWriter`) exposes only `Append` and `SaveAsync` — no
  update, delete, soft-delete, replace, or metadata-edit surface (proven by a reflection test).
- `AuditEvent` has no public methods or public setters; metadata is a read-only dictionary
  copied from the caller at construction.
- A DbContext-level safeguard rejects any `Modified`/`Deleted` AuditEvent entry on **every
  save path — synchronous `SaveChanges()`/`SaveChanges(bool)` and asynchronous
  `SaveChangesAsync(ct)`/`SaveChangesAsync(bool, ct)` all funnel through one shared guard**
  (`InvalidOperationException`) — persistence-level append-only enforcement implemented
  without broad refactoring (proven by sync and async tests).
- Direct privileged SQL/database writes remain outside every application guarantee.

## Transaction compatibility (for Phase 2)

`Append` stages the event into the current `ClinicDbContext` unit of work. Phase 2 mutation
integrations (e.g. finalize prescription + append `prescription.finalize`) will therefore
commit or roll back in the same SQL transaction as the mutation — one context, one
`SaveChanges`, no second database or external service. `SaveAsync` exists only for standalone
appends that are not coupled to a mutation; its failure handling detaches only the staged
audit events (verified by test).

## Deferred and unresolved

- **No feature integration yet**: login, MFA, logout, revocation, provisioning, grant changes,
  patients, visits, vitals, medication catalog, prescriptions, prescription PDF, and
  appointments produce no audit events. Phase 2 integrates mutations deliberately.
- **Read/download auditing deferred to Phase 3** (`patient.record.read`, `prescription.read`,
  `prescription.pdf.download`). Read/download writes cannot use the mutation-transaction
  model; Phase 3 must deliberately choose and test its failure policy (fail-closed vs
  fail-open with durable queueing). No read-audit guarantee is claimed.
- **Retention is unresolved**: duration is an operational/compliance decision; no application
  delete path exists; privileged database administrators remain outside application
  guarantees; production backup/retention policy must eventually account for AuditEvents.
- **Audit query authorization/UI** does not exist yet.

## Migration status

`20260916161906_AddAuditFoundation` — additive only: creates `AuditEvents`, its columns and
five indexes; Down drops the table. It alters no existing table, adds no foreign keys, and
leaves all prior migrations untouched. It has NOT been applied to ClinicDb; tests use isolated
`ClinicTests_*` databases only. Designer and snapshot agree with the model.

## Production limitations

The foundation alone provides no audit coverage of any endpoint. Production also still
requires the broader operational prerequisites documented in earlier phases (protected
storage, restricted operator access, backup/retention procedures covering AuditEvents, and
payload-safe logging). Audit Event protection (who may read audit data) is undefined until the
query slice exists.
