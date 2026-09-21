# Doctor Tablet Phase 1A-4D — Flutter notebook integration

Implemented only the Flutter integration over the existing 4A/4B/4C backend.

## State and UI

`NotebookCubit` owns the page list, selected detail (including RowVersion and revision
number), pagination, busy/error state, lifecycle controls, and an in-memory retry
envelope. It subscribes to both the patient context and authenticated session.
Changing patient, logging out, or expiring/replacing the session cancels requests
and clears state. Generation, patient, and session-identity checks reject late
responses. The UI also checks the active patient ID before rendering notebook data.

The Notebook section sits within the selected patient workspace; the patient
header remains outside its scrolling content. Arabic remains the default, with
English and RTL/LTR support. Times are displayed in the device's local timezone.
Doctor controls use validated session roles; assistants receive read controls and
receptionists receive no notebook section. Backend authorization remains decisive;
401/403 responses clear cached notebook data and block writes.

## Existing endpoints

Base: `/api/staff/patients/{patientId}/notebook/pages`

| Request | Use |
| --- | --- |
| GET base, `page`/`pageSize=10` | Page list, load more (maximum page 100) |
| POST base, `{title}` | Create and select returned page |
| GET `/{pageId}` | Detail and current server projection |
| POST `/{pageId}/revisions` | Multipart minimal foundation revision |
| POST `/{pageId}/finalize` | JSON `expectedRowVersion`; apply returned projection |
| POST `/{pageId}/amendments` | Multipart minimal amendment on finalized pages |

The list projection does not contain finalized status or RowVersion. Each bounded
list batch is hydrated with up to ten detail requests before publication. Both
list and detail patient IDs are validated. No backend contract was extended.
Creation sends only the title; there is no safe visit selector in this workspace.

## Revision and concurrency behavior

Each new submission generates a cryptographically random clientDraftId. One random
originDeviceId belongs to the app's NotebookCubit instance. It is an in-memory
app-session identifier, not a durable hardware identity.

The sole uploaded JSON payload is:

```json
{"formatVersion":1,"patientId":"...","pageId":"..."}
```

Multipart fields are expectedRowVersion, clientDraftId, and originDeviceId, plus
one `payload` JSON file. An uncertain response retains the original fields and
payload bytes for explicit retry using the backend's existing idempotency behavior.
New mutations are disabled while this retry is pending. The auth interceptor
clones finalized FormData before a one-time token-refresh replay.

After a revision acknowledgement, a detail read retrieves the current projection:
an exact replay may acknowledge an older revision and must not regress the UI.
If this read fails, the same submission remains available for retry. Finalization
uses the selected RowVersion and applies the returned page. Normal revisions are
disabled on finalized pages; the amendment foundation action remains available.

409 `page_changed` has its own localized message, preserves local metadata and the
retry envelope, and blocks further writes/retry until an explicit refresh/reopen.
Other 409 responses are distinguished from both page_changed and generic failures.
No automatic overwrite or merge occurs. Explicit refresh discards the minimal
pending envelope only after successfully reading current server state.

## Changed files

Under `apps/doctor_tablet/`:

- `lib/features/notebook/data/notebook_api.dart` (new)
- `lib/features/notebook/state/notebook_cubit.dart` (new)
- `lib/features/notebook/presentation/notebook_section.dart` (new)
- `lib/app/doctor_tablet_app.dart`
- `lib/features/patients/presentation/patient_workspace.dart`
- `lib/features/home/presentation/authenticated_shell.dart`
- `lib/features/auth/data/mobile_auth.dart`
- `lib/l10n/app_ar.arb`, `lib/l10n/app_en.arb`
- `lib/l10n/app_localizations.dart`, `app_localizations_ar.dart`, `app_localizations_en.dart`
- `test/auth_fixture.dart`
- `test/notebook_test.dart` (new)

Also this implementation report. Pre-existing backend/MVC edits are unrelated and
were not modified by this slice. `clinic_core` is unchanged.

## Validation and limits

Run in `apps/doctor_tablet` on 2026-09-21:

- `flutter analyze`: passed, no issues.
- `flutter test --timeout 60s`: passed, 54 tests (16 new notebook tests).
- `clinic_core` checks were not run because that package was unchanged.

Tests cover list success/empty/error, pagination, title-only creation, detail,
revision/RowVersion updates, exact retry after a lost response, multipart auth
refresh, finalization and read-only controls, amendment, stale-page conflict,
other conflicts, backend denial, wrong-patient rejection, patient switches with
late responses, logout, session expiry, role controls, and Arabic/English direction.

These are Flutter tests with a fake HTTP adapter; no live backend/device end-to-end
test was performed. No handwriting canvas, stroke data, local clinical database,
offline queue, conflict-resolution UI, PDF/export, backend change, or migration is
included. Retry state is deliberately lost on context/session teardown or restart.
An uncertain create response requires checking server state before another create;
creation is not automatically retried. Stop at Phase 1A-4D.
