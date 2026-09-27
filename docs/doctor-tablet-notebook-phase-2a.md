# Doctor Tablet Phase 2A — Multi-page notebook workflow

Scope: Flutter notebook navigation, selected-page ownership, metadata reads, and
tests. No backend API, backend migration, SQLCipher schema, queue protocol, or
conflict-resolution policy changes were made for this phase.

## Navigation and progressive loading

- A dedicated collapsible, 260-pixel-high lazy page list shows title, created and
  updated times, revision, finalized/draft lifecycle, and known local status.
- Metadata loads in server-provided batches of 10. Because the existing list
  projection omits lifecycle and RowVersion, each batch makes up to 10 bounded
  detail-metadata requests. Listing does not fetch ink or deserialize drafts.
- Previous/next follow the API's clinical ordering (newest created first), in
  both Arabic RTL and English LTR. Next loads another batch when necessary.
  Position shows the loaded count with `+` while more pages remain; no total is
  invented before pagination finishes.
- Doctor-only page creation validates a required trimmed title and uses a single
  in-flight operation to reject repeated taps. Only a successful server response
  prepends/selects the page; unrelated pages are not reloaded.
- The persistent patient header remains outside notebook scrolling. The page
  browser can collapse, leaving the canvas full-width. Changing patient clears
  the unsubmitted title field.

## Selected-page ownership and safety

- NotebookCubit owns the selected DraftHandle. InkPage borrows that handle and
  has an owner/patient/page key; it does not independently reopen or release it.
- Switching finishes a partial pen contact, locks the old controller, awaits its
  encrypted save, releases history/controller state, then reads and opens the
  destination. Save failure keeps the current ink editable and prevents the
  target request. A rejected restore can be closed without modifying its
  encrypted row, so an unreadable page does not trap other pages.
- A target restores only its own owner/patient/page-bound local draft. Undo/redo,
  pointer contacts, viewport transform, and amendment editing mode are not
  transferred between pages.
- If there is no local draft and the selected page has a current server revision,
  only that latest revision is fetched through the existing bounded, binding-
  validated decoder. Metadata is rechecked, then the draft is persisted encrypted
  before display. Failed download/persistence never opens an editable blank
  replacement. Existing local ink is never silently replaced by server content.
- Patient/account changes cancel and invalidate old operations, immediately hide
  the old workspace, and flush/release its handle. Late metadata, payload, restore,
  and queue callbacks cannot select a page in another workspace.
- Finalized pages remain read-only for ordinary ink. The existing explicit
  amendment and conflict-resolution workflows remain in force.

## Queue, badges, and memory

- Metadata-only SQLCipher reads supply per-page draft status and queue headers.
  Queue scans no longer materialize all queued payloads: replay loads one selected
  operation, duplicate checking loads only the page tail, and ACK transaction
  inspection is bounded to the head and successor. Resolution reads queue IDs and
  states without all payloads. Schema version remains 2.
- Badges distinguish local changes, local saved, queued, syncing, server synced,
  offline/saved locally, conflict, and sync failed. Unknown status is omitted.
  Status notifications occur on transitions, including starting an active stroke,
  rather than rebuilding the page list for every pointer move.
- A stopped/conflicted page does not block selecting or creating another page.
  Other pages continue foreground replay. An inactive page's ACK updates its list
  metadata and badge without selecting it or attaching its canvas.
- Normal inactive UI controllers and histories are disposed. Only metadata/status
  strings remain in the browser. The existing serialized foreground drain may
  temporarily restore one additional page handle to validate/persist its ACK; it
  owns no input surface and is released afterward. A failed encrypted save is an
  intentional exception: its unsaved memory is retained rather than discarded.
- Logout policy is unchanged: all drafts must flush successfully before logout;
  failed persistence refuses logout. Saved drafts and queued work remain encrypted
  and owner-scoped. No raw authentication tokens are stored with drafts/queue work.

## Files changed for this phase

- `apps/doctor_tablet/lib/features/notebook/state/notebook_cubit.dart`
- `apps/doctor_tablet/lib/features/notebook/state/local_drafts.dart`
- `apps/doctor_tablet/lib/features/notebook/state/notebook_sync_queue.dart`
- `apps/doctor_tablet/lib/features/notebook/data/encrypted_draft_store.dart`
- `apps/doctor_tablet/lib/features/notebook/data/sync_queue.dart`
- `apps/doctor_tablet/lib/features/notebook/ink/ink_controller.dart`
- `apps/doctor_tablet/lib/features/notebook/presentation/ink_page.dart`
- `apps/doctor_tablet/lib/features/notebook/presentation/notebook_section.dart`
- Arabic/English ARBs and the three generated localization Dart files.
- `apps/doctor_tablet/test/multipage_notebook_test.dart`
- `apps/doctor_tablet/test/draft_fixture.dart`
- `apps/doctor_tablet/test/notebook_test.dart` (latest-payload fixture support)
- `apps/doctor_tablet/integration_test/encrypted_draft_test.dart`
- This report.

Earlier phases already had uncommitted changes in this workspace, including
backend files. Those are not Phase 2A changes.

## Validation

The multi-page suite covers pagination without ink reads, previous/next ordering,
create/select and repeated-tap protection, separate A/B/A drafts, inactive
controller/history disposal, partial-pointer flush, failed-save navigation,
finalized/amendment lifecycle, selected latest-revision loading, failed remote
read safety, conflict badges/reopen, independent foreground sync, late patient
responses, account isolation, logout retention, and Arabic/English layout/header.

Verified on 2026-09-27:

- `flutter analyze`: no issues found.
- `flutter test --timeout 60s`: 162 tests passed, including 16 new multi-page tests.
- `flutter test integration_test/encrypted_draft_test.dart -d emulator-5554
  --timeout 60s`: 3 tests passed on the Pixel_Tablet Android emulator. These include
  real SQLCipher encryption/reopen/key rejection, queue upgrade/order, transactional
  conflict rollback, and the new metadata-only/owner-isolated query assertions.

## Remaining limitations

- Page catalog and fresh page metadata still require the existing foreground
  server APIs; this phase does not add a persistent offline metadata catalog or
  promise navigation to an unlisted page while offline.
- Only the selected page's latest server revision can be hydrated. There is no
  historical revision browser, arbitrary-history hydration, merge, reorder,
  thumbnail, background service, scanned-document annotation, or export.
- Existing local drafts take precedence even when server metadata is newer;
  conditional writes and explicit conflict resolution retain that safety boundary.
- A lost response to page creation has no new client-side/server idempotency
  contract. Creation is not automatically retried; refresh the server list before
  explicitly trying again. Concurrent taps while the request is active are blocked.
- Tests use synthetic pointer input and an Android emulator. No real-device
  pressure, palm rejection, hardware eraser, or latency validation is claimed.

Phase 2A stops here.
