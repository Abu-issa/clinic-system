# Doctor Tablet Phase 2B — Revision history and historical ink viewer

Implemented only revision inspection. No restore, rollback, copy-to-current,
comparison, merge, reorder, thumbnails, export, service, or native bridge.

## API and security

Added `GET /api/staff/patients/{patientId}/notebook/pages/{pageId}/revisions`.
It defaults to `page=1&pageSize=20`, accepts page sizes 1–100 and pages
1–1,000,000, and returns descending immutable revision-number order with one
lookahead row for `hasMore`. Flutter requests 20 metadata records at a time.

The response includes patient/page binding, pagination, current revision number,
and items containing only revision number, kind (`Created`, `Revision`,
`Amendment`), author staff identifier, creation timestamp, and payload availability.
It does not expose storage keys, stored-file IDs, payload bytes, retry identifiers,
or device IDs. Listing performs no private storage reads.

The route uses the existing `ClinicNotebookStaff` mobile/cookie selector,
`StaffSession` authorization and `NotebookRead` persisted permission/patient-scope
checks. Page lookup is constrained to the route patient. Password-only sessions,
invalid bearer credentials, missing permission, and out-of-scope access fail
closed. Each successful list durably records `notebook.revision.list` against the
page and patient with empty audit metadata. Audit failure prevents disclosure.
Existing no-store response policy is retained. No database migration was added.

Historical payloads reuse the existing private, audited, integrity-checked
`.../revisions/{revisionNumber}/payload` endpoint. Its successful responses now
include `X-Notebook-Patient`, `X-Notebook-Page`, and `X-Notebook-Revision` binding
headers. These are additive for existing clients; the new historical client
requires them before accepting bytes. The v2 payload itself binds patient/page,
but intentionally does not contain a server revision number.

## Decoding and display

- Selecting a revision fetches only that payload, bounded by the existing 4 MiB
  limit, and validates response binding and decoded patient/page binding.
- MessagePack v2 uses the existing strict vector decoder and `InkPainter`.
- Legacy JSON v1 metadata-only payloads, including leading JSON whitespace,
  display an empty page with an explicit legacy/no-stored-ink label.
- `Created` metadata records have no private payload: selection displays an empty
  creation page without making a payload request.
- Malformed bytes, unsupported formats, bad bindings, and read errors produce a
  localized safe failure message; raw server response bodies are never displayed.
- The selected page's title and finalized/draft state stay visible. Revision kind,
  author identifier, timestamp, selected revision, and latest indicator appear in
  the list. The patient header remains outside notebook scrolling.
- Arabic and English provide History, read-only historical labeling, legacy and
  creation notices, and Back to Current Page, with normal RTL/LTR layout.

## Separation and ownership

`NotebookHistory` owns only transient history metadata and one historical document.
It has no draft store, queue, InkController, or editing methods. `HistoryPanel`
renders with a clipped, repaint-bounded CustomPaint and no pointer Listener,
eraser, undo, submit, finalize, amendment, or conflict-resolution controls.

The normal NotebookCubit retains the exact current DraftHandle while its editable
canvas widget is absent. Returning uses that same handle/document and undo/redo
history. History neither saves/replaces the encrypted draft nor changes its server
base, queue, conflict, or amendment editing state. Current save/sync status remains
visible during history. Existing autosave and foreground queue processing retain
their normal independent behavior; browsing does not pause or trigger them.

Opening history is disabled while a stylus contact is active, so inspection does
not finish, cancel, or add an edit to the current stroke. Mutation guards also
reject current revision/finalization/conflict actions while history is visible.

History binds to the current server/staff owner, patient, and page. Closing it,
switching page/patient/account, or losing authorization cancels reads and clears
its state. Generation and selection checks reject late responses even if transport
cancellation cannot prevent completion. Read failures never touch local drafts.

Only one historical payload is retained: selecting another clears the previous
document before downloading; leaving clears all history metadata and ink. No
historical payload cache or encrypted historical archive is introduced.

## Verification (2026-09-27)

- `dotnet build backend/Clinic/Clinic.slnx`: succeeded, 0 warnings, 0 errors.
- `dotnet test backend/Clinic/Clinic.slnx --no-build`: 296 unit tests and 747
  integration tests passed; 0 failures, 0 skipped. Includes 3 new HTTP history
  tests for pagination/order/kinds, scope/MFA/mobile-cookie authorization, mismatch,
  bounded input, safe projection, response bindings, and audit failure behavior.
- `flutter analyze`: no issues found.
- `flutter test --timeout 60s`: 181 passed, including 19 history tests for metadata
  pagination, v2/legacy/Created views, dirty/queued/conflicted draft preservation,
  amendment mode, pointer safety, resource release, malformed/binding failures,
  late page/patient/account responses, exact return, and Arabic/English read-only UI.
- `git diff --check`: no whitespace errors.

## Files changed for Phase 2B

Backend:
- `backend/Clinic/src/Clinic.Api/Controllers/StaffNotebookPagesController.cs`
- `backend/Clinic/src/Clinic.Application/Notebook/INotebookStore.cs`
- `backend/Clinic/src/Clinic.Infrastructure/Repositories/NotebookStore.cs`
- `backend/Clinic/tests/Clinic.IntegrationTests/Api/StaffNotebookHistoryHttpTests.cs`

Flutter:
- `apps/doctor_tablet/lib/features/notebook/data/notebook_history_api.dart`
- `apps/doctor_tablet/lib/features/notebook/state/notebook_history.dart`
- `apps/doctor_tablet/lib/features/notebook/state/notebook_cubit.dart`
- `apps/doctor_tablet/lib/features/notebook/presentation/history_panel.dart`
- `apps/doctor_tablet/lib/features/notebook/presentation/notebook_section.dart`
- `apps/doctor_tablet/lib/features/notebook/ink/ink_controller.dart`
- Arabic/English ARBs and the three generated localization Dart files.
- `apps/doctor_tablet/test/notebook_history_test.dart`
- This report.

Earlier uncommitted changes from prior phases remain in the workspace.

## Limitations

- History requires an authorized online read; there is no offline history cache.
- Author is the immutable staff identifier, not a newly resolved display name.
- Pagination uses the existing page-number convention. Concurrent new revisions
  may shift offsets; the client deduplicates revision numbers. Reopening history
  starts a fresh metadata view; the latest indicator reflects the last list read.
- Backend and Flutter changes must deploy together for the new history feature:
  the viewer requires the new response binding headers and metadata endpoint.
- Historical viewing offers fitted-page rendering and normal screen scrolling;
  no history-specific zoom/pan controls were added.
- No real-device stylus, palm rejection, pressure, hardware eraser, or latency
  validation is claimed.

Stopped after Phase 2B.
