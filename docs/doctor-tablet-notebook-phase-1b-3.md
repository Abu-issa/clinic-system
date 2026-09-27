# Doctor Tablet Phase 1B-3: encrypted local drafts

## Storage and key handling

The app uses `sqflite_sqlcipher` 3.4.1 for a private `notebook-drafts-v1.db` database. Opening requires a nonempty random 256-bit key and a successful `PRAGMA cipher_version` check. There is no plain SQLite or plaintext-file fallback. Draft writes are transactional; SQLite uses full synchronous durability and memory-only temporary storage. Encoding and decoding run through Flutter `compute`, away from the input isolate on Android.

The key is generated with `Random.secure`, written and read back through `flutter_secure_storage`, and held under a separate notebook namespace. Android uses the plugin's Keystore-backed wrapping; the app does not directly store clinical drafts or keys in SharedPreferences. The secure-storage plugin manages its own encrypted backing values. Destructive `resetOnError` is disabled. A missing key for an existing database fails closed: the app neither generates a replacement nor deletes the database. Wrong keys also fail. Authentication logout clears only authentication credentials, not the notebook key.

Android backup and device-transfer rules exclude database and secure-storage backing data; the database cannot usefully transfer without its device-bound key. The SQLCipher release keep rules are included. No custom native Android bridge was added.

Package documentation: [SQLCipher plugin](https://pub.dev/packages/sqflite_sqlcipher), [secure-storage Android options](https://pub.dev/documentation/flutter_secure_storage/latest/flutter_secure_storage/AndroidOptions-class.html).

## Draft schema and binding

The encrypted `drafts` table contains:

| Column | Meaning |
| --- | --- |
| `owner` | Encoded API origin and authenticated staff ID; separates accounts and servers |
| `patientId`, `pageId` | Requested patient/page binding; together with owner form the primary key |
| `formatVersion` | Local ink document version, currently 1 |
| `payload` | Local JSON document with its own patient/page binding and versioned vector strokes/points |
| `serverRevision`, `serverRowVersion` | Nullable last-known server base metadata |
| `updatedAt` | UTC snapshot time |
| `syncState` | `localOnly`; never presented as server synced |

This JSON is an internal local format, not a server ink transport contract. It retains coordinates, pressure, timing, widths, colors, and stroke identifiers. Reopening checks owner, row patient/page/version, payload patient/page/version, and numeric/vector validity before rendering. A mismatch, corruption, unsupported version, missing key or storage failure hides ink and disables editing, with a localized retry message. No bad row is overwritten by an empty page. Restored stroke identifiers continue above the previous maximum. Undo/redo and viewport history are not persisted.

Server base metadata on a reopened local draft remains the metadata associated with that draft; opening it does not silently rebase it onto a newer server revision. Reconciliation is outside this phase.

## Autosave and state ownership

`LocalDrafts` belongs to the session/app, not the notebook widget. Handles own their controllers and save state. A page leaving the widget tree starts an immediate asynchronous flush of its old binding. This covers page changes, patient changes, and closing the page. A failed save retains that binding's dirty document in memory, rather than disposing it with the old widget. Reopening the same binding can recover it; another patient or staff account cannot render it.

Each committed edit, including undo/redo and whole-stroke erase, resets a 1.5-second debounce. A separate 30-second timer begins when the draft becomes dirty and does not slide with further edits, so sustained committed drawing still checkpoints. Writes are serialized per draft. Completion of an older snapshot cannot mark newer edits saved. Further input remains enabled during ordinary saves; the saved document is an immutable snapshot.

The app lifecycle observer immediately requests a flush on inactive, hidden, paused, and detached states. Page/lifecycle/logout boundaries first commit any partial pen stroke; an unfinished eraser gesture is cancelled. Platform suspension may limit the time available to finish a flush.

The English labels are **LOCAL CHANGES** and **LOCAL SAVED**, with Arabic translations. LOCAL SAVED appears only after a successful encrypted transaction or successful restore of a persisted draft, and is hidden while another pen stroke is active. Save failure retains LOCAL CHANGES and exposes a retry message. Save-error layout space is reserved so asynchronous save outcomes cannot move the canvas under the stylus. No SERVER SYNCED state is shown.

## Exact logout and expiry behavior

Explicit logout temporarily locks editing and finishes active pen strokes, then waits for all retained draft handles to flush. Any restore or write failure stops logout, leaves the session signed in, retains draft memory, and shows an explanation asking the user to keep the app open and retry local storage. It does not invoke the authentication logout request in that case.

After all drafts are safely persisted, authentication logout proceeds. Unsynced encrypted records and their secure key remain on the device. They can reopen after the same staff account signs in to the same server and selects the matching patient/page. There is no automatic draft deletion on logout.

Forced authentication expiry still hides protected patient UI immediately. It requests a local flush and retains any failed dirty handles under their original owner. It does not keep an expired session authorized. The same account can recover retained handles after reauthentication while this process remains alive.

## Validation

Completed from `apps/doctor_tablet` using `C:\src\flutter\bin`:

- `flutter analyze`: passed, no issues.
- `flutter test --timeout 60s`: passed, all 98 tests (24 new Phase 1B-3 unit/widget tests).
- `flutter test integration_test/encrypted_draft_test.dart -d emulator-5554 --timeout 120s`: passed, one real SQLCipher/secure-storage test on the Android Pixel Tablet emulator. This also built and installed the Android debug application successfully.

The automated suite covers schema round trips, row and payload patient/page/version mismatches, owner mismatch, encrypted-open/write configuration, missing-key safety, secure-key persistence failure, rejection of a noncipher engine, debounce, fixed dirty checkpoints, edits during writes, page/patient flushes, partial-stroke background flush, failed restore, failed-save labels, reopen, logout safety, and English/Arabic labels. Existing ink, navigation, patient-context and session tests remain included.

An Android emulator integration test uses real SQLCipher and secure platform storage with synthetic data. It checks database and sidecar bytes for plaintext markers and a plaintext SQLite header, key persistence, close/reopen stroke restoration, missing-key refusal, wrong-key rejection, and successful recovery with the original key. It cleans up only its dedicated test database and test key.

## Limits and phase boundary

- Android emulator validation is not physical-device or hardware-backed Keystore attestation. Physical-device pressure, palm rejection, latency and storage durability under power loss were not validated.
- SQLCipher's plugin supports Android/iOS/macOS; Windows/web have no fallback and safely refuse local draft editing when storage cannot open. Only Android was exercised natively here.
- Process termination, device loss, uninstall, clearing app data, or key loss can lose edits that have not completed an encrypted transaction. The app cannot guarantee a final callback on forced termination. Keep it open after a reported save failure.
- Drafts remain local-only and retained indefinitely. There is no server ink upload, backup/recovery/export workflow, sync queue, MessagePack contract, conflict UI, multi-page ink UI, or PDF/export.

Work stops at Phase 1B-3. Backend APIs and migrations were not changed.
