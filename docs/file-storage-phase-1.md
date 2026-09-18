# Secure file storage — Phase 1 foundation

Phase 1 adds a provider-independent private file-storage foundation on the committed audit
baseline (`f548588`). It stores nothing clinical yet: there are no attachment endpoints, no
patient/visit/prescription linkage, no upload/download API and no audit integration. Future
phases (attachment links + protected API, then per-feature rollout) build on exactly these
primitives. One additive migration (`AddFileStorageFoundation`) is generated but NOT applied;
ClinicDb was not touched.

## Architecture

Clean Architecture boundaries are preserved:

- **Domain** (`Clinic.Domain.Entities.StoredFile`): immutable metadata row — Id, StorageKey,
  OriginalFileName, ContentType, SizeBytes, Sha256, CreatedAtUtc (TimeProvider-sourced, UTC),
  CreatedByStaffId (nullable, trimmed, ≤ 450, like audit actors). Constructor validation only;
  the application never mutates a row after creation. Deliberately NO foreign keys and NO
  Status column: no staged-upload workflow exists yet, and adding one now would be speculative;
  the phase-2 design can add a column with an additive migration if a staged workflow arrives.
- **Application** (`Clinic.Application.Storage`):
  - `IFileStorage` — provider-neutral contract: `StageAsync(Stream)`, `PromoteAsync`,
    `OpenReadAsync`, `ExistsAsync`, `DeleteAsync`. Streams only; no byte-array materialization,
    no S3 SDK types, no IFormFile, no HTTP types, no provider paths.
  - `IStoredFileStore` — metadata persistence (single-row `SaveAsync` only; no update/delete:
    rows are create-once and no user-facing hard-delete exists).
  - `StoredFileService` — the minimum reusable store flow (below).
  - `FileStorageOptions` — strongly typed configuration with `IsValid()`.
  - `FileSizeLimitExceededException` / `EmptyFileException` — future HTTP layers will map them.
- **Infrastructure**: `LocalFileStorage` (private filesystem provider) and `StoredFileStore`
  (ClinicDbContext metadata persistence, detached on save failure like the other stores).
- **API**: DI registration only. No endpoints.

## Storage keys and safety

Keys are server-generated, opaque, provider-neutral: `clinic-files/{32 lowercase hex}` (final)
and `clinic-files/_staging/{...}` (non-final staging namespace invisible to `OpenReadAsync`).
They are never derived from client input, so original filenames, patient names, MRNs, diagnoses
or medication names can never reach a storage path. The Domain entity re-validates the exact
key shape; `LocalFileStorage` additionally:

- resolves every key to a full path that must remain inside the private root (defeats `../`
  traversal, absolute paths, separator injection, drive/UNC forms and Windows reserved names),
- refuses keys containing `\`, `:`, NUL, `..` or extra segments,
- stages into the reserved staging sub-namespace on the same volume and promotes with an atomic
  `File.Move` (no overwrite), so a promoted object appears all-or-nothing.

The original filename is bounded (≤ 255), trimmed, control-character-free **display metadata
only**. It is never used for paths, keys, execution or storage-path construction.

### Reparse point / junction / symlink safety (hardening gate)

String-prefix containment cannot see through reparse points: a directory junction planted at
`<root>\clinic-files` (→ `C:\outside`) would pass a `GetFullPath` prefix check while redirecting
otherwise-valid keys outside the private tree. `LocalFileStorage` therefore refuses any
**existing path component below the root** that carries the Windows `ReparsePoint` attribute
(checked on every resolve, for all five operations, including at promote time). A real
junction-based regression test proves a planted junction turns every store/open into a
controlled `IOException` with nothing written to the junction target.

Trust assumption, stated exactly: the storage **root itself** is operator-controlled — a
principal that can plant reparse points inside the configured root already has filesystem write
access to clinical storage, which no application can prevent. The application's own operations
can never plant one: it only creates the fixed-named `clinic-files/` and `clinic-files/_staging/`
directories and hex-named files, and refuses to traverse anything reparse-flagged below the
root. Operators must configure a private, application-owned root (not a shared, user-writable
directory) as a deployment prerequisite.

## Local development provider

`LocalFileStorage` serves development/test (and an explicitly opted-in local production; see
configuration). Its configured private root (default `./data/files`, relative to the API
working directory; `backend/Clinic/src/Clinic.Api/data/` is gitignored so runtime bytes are
never committed):

- is never inside wwwroot (startup validation refuses such a root), and no static-file serving
  exists for it — files are only reachable through `IFileStorage`,
- is created on first use; no user-specific absolute path is committed to configuration,
- keeps every object inside `clinic-files/` beneath the root.

## Size, streaming and hash behavior

- `StageAsync` streams in one pass with a 80 KiB buffer while **counting actual bytes** (never
  trusting any declared length) and computing SHA-256 incrementally in the same pass; large
  files never require full in-memory buffering.
- The configured `FileStorage:MaxFileSizeBytes` is the absolute safety ceiling (validated at
  startup to 1..1 GiB; default 25 MiB). Exceeding it aborts mid-stream with
  `FileSizeLimitExceededException`. Zero-byte payloads are rejected (`EmptyFileException`).
  Future feature modules must enforce their own stricter, feature-specific limits on top —
  no single clinical limit is invented here.
- SHA-256 is persisted as 64 lowercase hex characters. It supports later integrity checks,
  duplicate detection and restore verification. **No deduplication happens**: identical bytes
  may legitimately be two different clinical records (the Sha256 index is deliberately
  non-unique).

## DB + object consistency model

A database transaction cannot commit a filesystem/S3 object, so `StoredFileService.StoreAsync`
uses this documented compensating order:

1. Stage the bytes (staging object only; size + SHA-256 measured).
2. Generate the final opaque key and the `StoredFile` metadata (server facts only).
3. Promote the object to its final key (atomic move).
4. Commit metadata (single-row `SaveChanges`).

- Storage failure during staging → provider deletes its own staged temp; nothing persisted.
- Failure during promotion → staged cleanup; no final object, no metadata.
- Metadata save failure → **no row was committed** (single-row save atomicity), so the service
  deletes the promoted object as compensation. Tests prove neither bytes nor metadata survive.
- Residual risks, stated honestly: a hard process crash between promotion (3) and commit (4)
  can leave an orphaned object with no metadata (harmless: nothing references it; orphan
  reconciliation is a future operational concern), and if the compensating delete itself fails
  an orphaned object remains while the original error propagates — a dedicated test proves the
  primary metadata failure is never masked by a compensation failure. Metadata-without-object
  requires both the save to report success and the object to vanish afterwards, which the
  local provider's atomic move cannot produce; a future S3 provider must re-prove the same
  invariant. No distributed-transaction guarantee is claimed anywhere.

## Delete / retention semantics

`IFileStorage.DeleteAsync` is an internal compensation/cleanup primitive (failed uploads,
staged cleanup, test teardown). It is NOT an application-user deletion feature: no HTTP surface
can delete anything in this phase, and future attachment lifecycles will decide retention and
removal policy for clinical content. `IStoredFileStore` has no delete at all: stored-file rows
are create-once.

## Key collision behavior

Promotion uses an atomic move with `overwrite: false`. A GUID-based key collision (or any
pre-existing object at the target key) is a controlled `IOException`: the existing object is
byte-for-byte untouched, the staged object remains for caller cleanup, and no metadata row is
persisted. No retry complexity is added — 128-bit random keys make collisions practically
impossible, and a collision failing closed is the correct outcome.

## Configuration and production safety

```json
"FileStorage": {
  "Provider": "Local",              // only "Local" exists; startup rejects anything else
  "LocalRoot": "./data/files",
  "MaxFileSizeBytes": 26214400,     // absolute ceiling; per-feature limits come later
  "AllowLocalInProduction": false
}
```

Startup validation (`ValidateOnStart`, following the PrintLicensing pattern) enforces: Provider
= Local, a bounded safe LocalRoot path, MaxFileSizeBytes within 1..1 GiB, LocalRoot not inside
wwwroot, and — the chosen production policy — **production refuses Local storage unless
`AllowLocalInProduction=true`**, an explicit reviewed operator opt-in. No S3 provider,
bucket, endpoint or credential is configured or referenced anywhere; nothing secret is
committed. Integration-test hosts run with `Production` environment and set the opt-in with
per-host disposable temp roots, which are cleaned up on dispose.

## Content type policy (honest limits)

The declared content type and original filename are stored as bounded metadata. No MIME
allowlist is enforced by the generic foundation (feature-level allowlists belong to future
features), and — deliberately — no content sniffing, malware scanning or sandboxing exists in
this phase. Content-Type must never be trusted for execution or inline rendering decisions;
future download endpoints must serve attachments (`Content-Disposition: attachment`), never
inline HTML rendering, and a scanning decision remains an explicit future requirement.

## Audit integration status

None yet — Phase 1 has no endpoints, so nothing is audited and no audit migration changed.
The future attachment API is designed to emit semantic events (`file.upload`, `file.download`,
`file.link`, `file.remove`) through the existing Phase 3 access-audit mechanism, with opaque
resource IDs and metadata limited to machine-safe tokens (never filenames, clinical text,
content or gratuitous hashes).

## What Phase 1 deliberately does NOT include

- No attachment/link entities (Patient/Visit/Prescription/LabRequest coupling), no upload or
  download endpoints, no patient uploads, no labs/imaging/archive/notebook usage.
- No S3/object-storage implementation, no production provider decision, no bucket config.
- No antivirus/content inspection, no MIME allowlist, no retention/purge policy.
- No orphan-object reconciliation job (documented as a future operational concern).
- No Prescription-PDF persistence change (printing still renders ephemeral bytes).

## Verification (2026-09-17)

- Build: 0 warnings, 0 errors.
- Unit: 197 passed (27 new: StoredFileDomainTests 8, FileStorageTests 19) — covering exact
  byte preservation, hash/size correctness, TimeProvider timestamps, opaque keys, hostile
  filenames (`../`, `..\`, `C:\`, UNC, NUL), duplicate names, zero-byte rejection, over-limit
  rejection, mid-stream failure cleanup, save-failure compensation, missing-object controlled
  null, root-escape rejection, delete idempotence, options validation.
- Integration: 364 passed (5 new StoredFilePersistenceTests) — metadata round-trip, nullable
  actor, unique StorageKey SQL backstop (SqlException on duplicate), no foreign keys/cascade
  coupling, full service flow over real SQL + filesystem, injected metadata-save failure
  leaves no row and no object.
- EF `has-pending-model-changes`: clean after `AddFileStorageFoundation`; migration is
  additive (one new `StoredFiles` table + unique StorageKey index + Sha256/CreatedAtUtc
  indexes; no existing table touched; Down drops only the new table).
- `git diff --check`: clean. ClinicDb untouched; migration not applied; nothing committed.

## Pre-commit hardening gate (2026-09-17)

Focused re-review of the finished Phase 1, before commit:

1. **Reparse points/junctions** — confirmed the original prefix-containment could be
   redirected by a junction planted below the root. Fix: `LocalFileStorage` now refuses any
   existing path component below the root carrying the `ReparsePoint` attribute (all
   operations, checked per resolve). Regression test `DirectoryJunctionBelowRootIsRefusedAndWritesNothingOutside`
   creates a real junction and proves every store/open is refused with nothing written to the
   target. Root-itself trust assumption documented (operator-controlled; app operations can
   never create reparse points).
2. **Cancellation cleanup** — new test `CancellationMidStreamLeavesNoObjectAndNoMetadata`
   proves a mid-stream `OperationCanceledException` propagates with original semantics,
   deletes staged bytes, and persists nothing (input-stream exceptions were already covered).
3. **Compensation** — reconfirmed by existing tests (stage ✓ → promote ✓ → save ✗ ⇒ object
   deleted, no row). New `CompensationFailureKeepsOriginalFailureAndDocumentsTheOrphan`
   proves that when the compensating delete itself fails, the primary metadata failure still
   propagates and the orphaned object (no dangling metadata) is exactly as documented. No
   distributed-transaction claims added.
4. **Collision** — new `PromoteNeverOverwritesAnExistingObject` proves `PromoteAsync` uses
   `overwrite: false`: a collision is a controlled `IOException`, the existing object is byte-
   for-byte unchanged, no metadata persists for the failed operation. No retry complexity.
5. **Configuration recheck** — wwwroot and Production-Local opt-in validations confirmed in
   Program.cs; grep proves no credentials/secrets/bucket config anywhere; all test storage
   roots are unique `Path.GetTempPath()` directories (`ClinicTests_files_*`/`ClinicUnit_files_*`),
   never the repository or a real clinical directory.
6. **Latent bug found and fixed by the new tests**: staged-object cleanup previously validated
   staging keys against the final-key shape, so the service's `finally` cleanup silently no-op'd
   (the thrown `ArgumentException` was swallowed). `DeleteAsync` now validates each key against
   its own prefix (final or staging), and the promote-collision test proves staged cleanup
   actually removes the staged object. The `PromoteAsync` contract comment was corrected to
   state that a failed promotion leaves the staged object for caller cleanup.
