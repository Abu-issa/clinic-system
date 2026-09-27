# Doctor Tablet Phase 1B-5B

Explicit conflict resolution for the existing encrypted notebook drafts and durable queue. No backend endpoints, backend migrations, dependencies, background services or database schema changes were introduced in this phase.

## Conflict UX

A localized inline panel appears for a conflicted notebook page. Keeping it inside the existing workspace leaves the patient identity/header visible. It displays the local page title, last successful local save time, queued revision count, and known server revision/update time. Known metadata is scoped to that patient/page; it is not presented as an automatically refreshed server view. Raw server bodies and RowVersion text are not displayed in the conflict panel.

There are three resolution choices:

1. **Keep for later** collapses the choices, retaining a conflict banner that can reopen them. It performs no request or local write and preserves the conflict and queue.
2. **Discard mine** first shows an explicit destructive warning. A second confirmation is required before any server read or data deletion.
3. **Save mine as new revision** explicitly requests a new revision from the current local ink. The panel explains that finalized pages use amendments and that ink is not merged.

Resolution actions are disabled while an action is running. Success and failure messages are localized in English and Arabic. Finalization remains blocked during conflict. A stable key on the ink widget preserves its live draft handle when the conflict panel appears or disappears.

## Discard behavior

After confirmation, fetch current page metadata, read its immutable revision payload through the existing private endpoint, validate patient/page and bounded ink content, then read metadata again to ensure the revision/RowVersion did not change during the read. Version-2 vector ink is restored; supported historical version-1 metadata-only payloads restore an empty ink document. A page with no revisions also restores an empty document.

Only after successful reads does an encrypted transaction replace this page's local draft with the server document, update its revision/RowVersion base and delete this page's conflicting queued operations. The visible ink and undo/redo state are replaced after commit. This avoids leaving an unexplained blank local page over an existing server ink revision. Other pages' drafts, payloads, identities and queue sequence values remain unchanged. Failed reads, malformed/bound-to-another-page payloads, concurrent server changes and transaction failures retain the conflict and local work.

## Save mine as a new revision

Read fresh server metadata first. On the first explicit attempt, create a fresh clientDraftId, use that metadata's RowVersion, preserve the exact serialized current local ink snapshot, and choose the existing revision or amendment endpoint according to the server's finalization state. No geometric merge occurs. Existing server revision history is retained.

Before submitting, persist the resolution envelope inside the encrypted draft payload while leaving the original queue and CONFLICT state intact. Successful submission requires the existing validated server ACK and matching refreshed detail. Only then does the encrypted transaction install the acknowledged local base and remove the resolved page's original queue set. All queued snapshots for that page are superseded by the doctor's explicitly chosen current snapshot; unrelated work is not reordered.

Timeouts or local commit failures retain the resolution envelope, including exact bytes and identifiers, across restart. Automatic drain never submits it. A later explicit Save mine action retries that same logical resolution to avoid duplicate revisions. Definitive submission rejection allows a subsequent explicit action to fetch a new base and create another new logical attempt. A failed metadata read does not discard an earlier uncertain resolution identity.

## Queue transaction and isolation

`completeResolution` uses a SQLCipher transaction covering both draft replacement and page-scoped queue deletion. It verifies the existing draft is still CONFLICT, the exact ordered set of queued IDs still matches, the head remains conflicted, and the owner/patient/page context remains current. Context invalidation detected before commit throws and rolls back both changes.

The queue head remains CONFLICT throughout server reads and upload. Other pages may continue foreground draining. No automatic merge, rebase, conflict clearing or last-write-wins path is added. The optional durable resolution envelope is stored inside the existing encrypted local payload; no plaintext files or new schema version are needed.

Resolution checks the currently authenticated doctor, server/staff owner binding, active patient and selected page. Late results cannot publish into another workspace. Account/patient changes invalidate the operation; an already accepted server revision is retained in server history and its uncertain local envelope can be handled after returning to the original owner/page. Another authenticated doctor cannot resolve or delete the original owner's queued work. Finalized pages remain finalized and route explicit saves through amendments, preserving existing server lifecycle enforcement.

## Verification

- `flutter analyze`: **no issues**.
- `flutter test --timeout 60s`: **146 passed**.
- `flutter test integration_test/encrypted_draft_test.dart -d emulator-5554 --timeout 120s`: **3 passed** on the Android Pixel Tablet emulator.

New tests cover the localized conflict panel and destructive confirmation, Cancel/no mutation, failed refresh, page-only discard, payload binding and concurrent server changes, latest RowVersion/new ID/unchanged ink, ACK success, failed rebase, local commit failure, durable uncertain retry, stopped automatic replay, other-page draining, patient/account changes, another authenticated owner, finalization blocking, amendment resolution and restart.

The native SQLCipher test deliberately fails after the draft INSERT and queue DELETE but before COMMIT, reopens the database, and verifies both changes rolled back. It then verifies a successful transaction clears only the chosen page without changing another page's queue identity, bytes or sequence. Prior upgrade/encryption/key/reopen tests also pass. Emulator testing does not validate real-device stylus behavior, pressure, palm rejection or latency.

## Primary files

- `presentation/conflict_panel.dart`, `presentation/notebook_section.dart`: inline flow and stable ink widget identity.
- `state/notebook_cubit.dart`, `state/local_drafts.dart`: explicit resolution, durable attempt and committed-state installation.
- `data/notebook_api.dart`: bounded private revision read for confirmed discard.
- `data/local_ink_draft.dart`, `data/sync_queue.dart`, `data/encrypted_draft_store.dart`: optional encrypted resolution envelope and atomic completion.
- English/Arabic ARB and generated localization files.
- `test/conflict_resolution_test.dart`, supporting fixtures, and `integration_test/encrypted_draft_test.dart`.

## Limits

Resolution requires server access. Concurrent server edits may cause another conflict, and failed/uncertain operations deliberately remain stopped until the doctor acts again. Discard restores the latest readable server state, which may include a previously accepted but ambiguously acknowledged resolution. It does not erase server history. Unsupported or unavailable server payloads prevent discard rather than deleting local work.

There is no geometric merge, visual comparison, general cross-device notebook hydration UI, multi-page management, Android background service, PDF/export or native stylus bridge. Phase 1B-5B stops here.
