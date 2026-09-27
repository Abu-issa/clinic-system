# Doctor Tablet Phase 1B-5A

Implemented a durable encrypted foreground notebook sync queue. Scope stops at Phase 1B-5A. This phase changes Flutter only; existing backend APIs and migrations are unchanged.

## Encrypted schema

The existing SQLCipher database upgrades locally from schema 1 to 2, preserving drafts and the existing secure-platform-storage encryption key. `SyncQueue` stores:

| Column | Purpose |
| --- | --- |
| sequence | Autoincrement ordering key |
| owner, patientId, pageId | Server/staff and clinical binding |
| operationType, amendment | Revision or amendment |
| payload | Exact MessagePack bytes in an encrypted BLOB |
| expectedRowVersion | Immutable base captured at enqueue |
| resolvedRowVersion, predecessorId | Ordered successor dependency |
| clientDraftId, originDeviceId, boundaryName | Stable replay identity and multipart configuration |
| createdAt | UTC epoch milliseconds |
| attemptCount, nextAttemptAt | Durable retry scheduling |
| state, errorCode, httpStatus | Queue state and allowlisted safe failure information |

There is a unique constraint on `(owner, clientDraftId)` and an index on `(owner, patientId, pageId, sequence)`. No tokens, passwords, server response bodies or exception text are stored. SQLCipher remains mandatory; there is no plaintext fallback. Temporary SQLite storage remains memory-only.

## Enqueue, ordering and ACKs

The existing submit action now snapshots ink into the queue after successful encrypted local persistence. Local autosave continues independently. Multiple submitted snapshots can wait offline; drawing remains available during networking. Duplicate logical IDs cannot create duplicate pending work, and repeated submission of an unchanged queue-tail snapshot coalesces.

Snapshot bytes, IDs, original base and operation type stay fixed. Later operations on the same page depend on the preceding operation. Since a future server RowVersion cannot be known while offline, the predecessor's confirmed ACK binds `resolvedRowVersion` exactly once, before the successor's first attempt. This is not a rebase against arbitrary refreshed server state. The captured `expectedRowVersion` remains unchanged for auditing.

Only the head of a page can upload. Writes are globally serialized, which also serializes writes per page. Successful sync requires the existing valid ACK plus matching fresh detail and successful encrypted local metadata persistence. Only then does a database transaction delete the exact matching queue head and bind its successor. Head identity, sequence and bytes are checked; acknowledging a successor out of order fails. Uncertain replies and crashes retain the original envelope for idempotent replay.

Finalization remains blocked while queued or unsynced ink exists. Acknowledgement of an older snapshot does not mark newer local edits as SERVER SYNCED.

## Foreground retry

Drain runs on startup/authenticated context availability, foreground resume, explicit submit/retry, and a 15-second foreground scheduler. Network reachability is established by actual requests rather than an optimistic connectivity flag. After connectivity returns, eligible work retries on a subsequent tick.

Network/5xx failures use durable exponential delays of 5, 10, 20, 40, 80, 160, then 300 seconds maximum. 408/429 also back off. No more than 32 operations are processed per drain; a failed page is visited only once in a drain. There is no tight loop or clinical-work deletion after a retry count. Explicit retry may bypass time delay, but cannot bypass a stopped page.

The existing coordinated 401 refresh preserves exact multipart replay. Unresolved authorization, 403/404, other non-transient HTTP/lifecycle errors, malformed bindings and missing local drafts stop automatic retry as SYNC FAILED. `409 page_changed` persists CONFLICT and blocks all successors of that page. Other pages can continue. Neither refresh nor restart clears stopped queue state.

## Isolation and lifecycle

Owner is the existing encoded server URL/staff ID binding. Queue queries and processing are restricted to the currently authenticated doctor owner. The payload must match the queue patient/page before transmission. Upload paths come from that validated envelope, never the currently visible patient.

Changing the selected patient can leave another patient's valid queue work running, but its ACK cannot populate the visible patient's notebook. Session/account changes invalidate the drain and cancel transport. Logout retains encrypted queue rows and draft snapshots; existing failed-local-flush protection remains. Different accounts cannot enumerate or process another owner's queue. Pausing the app cancels in-flight draining and preserves uncertain operations; there is no Android background service.

Live draft handles are held through replay and released only when detached, protecting page open/close races. Storage errors retain queue work. No automatic merge, rebase, discard or destructive recovery exists.

## UI states

English and Arabic labels distinguish LOCAL CHANGES, LOCAL SAVED, QUEUED, SYNCING, SERVER SYNCED, OFFLINE — SAVED LOCALLY, CONFLICT and SYNC FAILED. Dirty/failed local writes cannot show saved states. Queue errors and conflicts remain visible without exposing server bodies. The retry action is available for retryable queued work. SERVER SYNCED requires confirmed persistence and no remaining queue work for that page.

## Verification

- `flutter analyze`: **no issues**.
- `flutter test --timeout 60s`: **130 passed**.
- `flutter test integration_test/encrypted_draft_test.dart -d emulator-5554 --timeout 120s`: **2 passed** on the Android Pixel Tablet emulator.

Tests cover enqueue/persist ordering, duplicate protection, multiple snapshots and ACK chaining, reopen, successful drain, automatic foreground startup, backoff, 401 exact replay, stopped 409/403/404/lifecycle responses, owner isolation/logout, in-flight account changes, patient-specific routes, page-handle lifetime, missing/mismatched drafts, exact queue removal, and localized labels. Existing ink, viewport, autosave, auth and sync tests pass.

The native SQLCipher tests exercise the real schema-1 upgrade, queue BLOB round-trip, duplicate constraint behavior, failed out-of-order ACK, durable scheduling, transactional successor binding, owner filtering, secure-key reopen/wrong-key rejection, and absence of plaintext synthetic clinical markers in the database and sidecars. The emulator was stopped after testing. This does not validate real-device stylus pressure, palm rejection or latency.

## Files and limitations

Primary changes: `data/sync_queue.dart`, `data/encrypted_draft_store.dart`, `data/local_ink_draft.dart`, `state/notebook_sync_queue.dart`, `state/local_drafts.dart`, `state/notebook_cubit.dart`, notebook presentation widgets, app lifecycle wiring, localization sources/generated files, queue/sync tests and the SQLCipher integration test. No dependencies were added in this phase.

Operations enter the queue through explicit submit; autosave alone does not upload every edit. Sync requires the authenticated foreground app and may wait for its next scheduler tick. Stopped failures/conflicts require later explicit handling; resolution/merge UI is deferred. There is no background service, remote ink hydration, multi-page management, PDF/export or native bridge. The queue retains full snapshots without automatic pruning; large backlogs consume encrypted storage and should be included in future capacity/performance testing.
