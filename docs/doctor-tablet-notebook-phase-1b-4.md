# Doctor Tablet Phase 1B-4

Implemented real vector ink upload through the existing notebook revision and amendment APIs. The encrypted local draft remains the source of unsynced work. No database migrations were added or applied to main ClinicDb. Integration tests use their isolated test database. No clinic_core changes.

## Contract

New uploads use uncompressed MessagePack, `application/msgpack`, formatVersion **2**. Maps are written in fixed insertion order and stroke/point order is preserved. The root is:

```text
{
  formatVersion: 2,
  patientId: string,
  pageId: string,
  pageWidthUnits: 210.0,
  pageHeightUnits: 297.0,
  strokes: [{ id: integer, color: ARGB uint32, width: logical units,
              points: [[x, y, pressure-or-null, relativeTimeMicros], ...] }]
}
```

Timing starts at zero for each stroke and is monotonic. Coordinates are logical page units, independent of viewport pan/zoom. Null pressure retains fixed-width behavior. Existing version-1 JSON revisions remain accepted/readable under their original 16 KiB limit; existing stored bytes are never rewritten. Local encrypted draft JSON stays version 1 and is distinct from the version-2 server contract.

## Validation and storage

- Payload: at most 4 MiB; multipart request: payload limit plus 16 KiB overhead.
- At most 4,096 strokes, 20,000 points per stroke, 100,000 total points; nonempty point arrays.
- Unique nonnegative stroke IDs up to 2^53−1; ARGB color up to uint32 maximum.
- Exact dimensions 210 × 297; x/y bounded to page; width 0.01–10; pressure null or 0–1. Numeric samples must be finite.
- Relative timing 0–86,400,000,000 microseconds (24 hours), monotonic, first sample zero.
- Strict known fields, no duplicate keys, malformed/trailing bytes rejected. Backend parsing is fixed-depth streaming; Flutter preflights byte/container/depth limits before decoding allocations.
- Patient/page must match the authorized database page and route. Existing authorization, private read auditing/integrity, StoredFile, IFileStorage, idempotency, RowVersion checks and stage/promote/compensate workflow are reused.

## Sync lifecycle and safety

Manual sync finishes active ink, takes an immutable document snapshot and serializes it off the UI isolate. Before networking, it stores the exact bytes, patient/page binding, expected RowVersion, clientDraftId, originDeviceId and amendment flag inside the existing encrypted draft. No plaintext file is created.

SERVER SYNCED requires a valid server acknowledgement, matching refreshed revision/RowVersion, unchanged current snapshot, and successful encrypted metadata persistence. Edits made during upload remain local changes even after that older snapshot is acknowledged. Local save failures never produce a saved claim. Finalization requires the current ink to be synced, disables drawing while finalization runs, and updates the local base. Finalized pages accept new ink only through explicit amendment mode and the same serialization path.

Uncertain submissions retain one durable retry envelope. Reopen/retry reuses exact ink bytes and both identifiers; it does not create a new logical revision. Refresh-token replay clones FormData, preserving the entire multipart body. Explicit subsequent attempts may generate a new multipart boundary while preserving the exact payload bytes and identity.

409 preserves the local document, serialized envelope and original server base and persists CONFLICT. Refresh/reopen does not clear the block. Finalization conflicts also persist. There is no merge or rebase UI. Network failures persist OFFLINE — SAVED LOCALLY only when the encrypted local write succeeds. LOCAL CHANGES, LOCAL SAVED, SYNCING, SERVER SYNCED, CONFLICT and OFFLINE — SAVED LOCALLY are localized in English/Arabic.

Patient/session generation checks prevent late responses from being published into another active context. Page/patient switches retain the existing local flush and binding checks. Logout retains encrypted drafts and envelopes; failed local flush blocks logout. No clinical draft is silently deleted.

## Phase-specific files

- Backend: `NotebookInkPayload.cs`, `NotebookPayloadService.cs`, `StaffNotebookPagesController.cs`, `Clinic.Application.csproj`, `backend/Directory.Packages.props`.
- Backend tests: `NotebookInkPayloadTests.cs`, `StaffNotebookInkHttpTests.cs`.
- Flutter data: `server_ink_codec.dart`, `notebook_api.dart`, `local_ink_draft.dart`.
- Flutter state/UI: `local_drafts.dart`, `notebook_cubit.dart`, `ink_page.dart`, `notebook_section.dart`.
- Flutter localization: both ARB files and generated localization Dart files; dependencies in pubspec.yaml/lock.
- Flutter tests: `ink_sync_test.dart`, updated `notebook_test.dart`. Earlier Phase 1B-3 changes remain in the working tree.

## Verification

- `dotnet build backend/Clinic/Clinic.slnx`: passed, zero warnings/errors.
- `dotnet test backend/Clinic/Clinic.slnx --no-build`: **296 unit + 744 integration = 1,040 passed**, no failures/skips.
- `flutter analyze` in apps/doctor_tablet: no issues.
- `flutter test --timeout 60s`: **112 passed**.

Coverage includes bounded/malformed real payloads, binding, idempotent server retry, stale RowVersion, amendments and private read integrity; Flutter round-trip, stable coordinates, acknowledged state transitions, durable retry after reopen, full 401 multipart replay, conflicts, concurrent edits, local save failure after ACK, finalization, patient switching and localized states. Existing ink, viewport, encrypted storage and autosave tests also pass.

## Limits

One retained uncertain submission is not a durable multi-operation queue. Sync is user-triggered; no background sync jobs. The app restores its own encrypted local draft; downloading remote revision ink into a new device is not implemented. Conflict resolution is deferred. Oversized local documents remain locally retained but cannot upload past contract limits. No multi-page management, PDF/export, native bridge or hardware eraser support was added. Pressure, palm rejection and latency have not been validated on real hardware. Phase 1B-4 stops here.
