# Doctor Tablet Phase 1A-3B: Flutter patient search and context

## API and scope

Uses the existing authenticated `MobileAuth.api` Dio instance and its coordinated refresh
interceptor. `POST /api/mobile/staff/patients/search` receives only a JSON search term, page,
and page size. No staff/doctor IDs, roles or scopes are sent. Authorization remains server-side.
Search accepts 2–100 trimmed characters and rejects controls. Each page requests 10 results;
load more is bounded to page 100 and stops when `hasMore` is false. Duplicate load-more requests
are blocked and overlapping patient IDs are deduplicated. A new search resets pagination.

Only `patientId`, `fullName`, `medicalRecordNumber`, `dateOfBirth`, and numeric `allergyStatus`
are mapped. Allergy values map to Unknown, NoKnownAllergies and HasKnownAllergies; unknown or
unrecognized values never imply no allergies. Nullable MRN/DOB show localized “Not recorded”.
DOB is displayed using locale-aware date formatting; no age calculation or extra clinical fields
are introduced. No patient data, search terms or responses are logged or written to storage.

## State and patient switching

`PatientContextCubit` owns the active projection, search results, term, pagination, busy status,
and safe error classification. It observes `SessionCubit`; token refresh keeps the same context,
while logout, expiry and any new authenticated session discard it. Cancellation and generation
checks prevent late search results from restoring old data. A new login for the same staff user
still starts with no patient selected. This is persistence across the live workspace only,
not disk persistence or restoration across app restarts.

`PatientWorkspace` displays a fixed `PatientHeader` outside the scrolling search/results region.
It includes full name, MRN, DOB and a text-plus-icon allergy indicator. Searching and loading more
do not silently change the active patient. Selecting another patient prompts for explicit
confirmation naming both patients. Cancel retains the original context; confirm replaces the
entire projection. Session loss dismisses the dialog and removes all patient UI. Refresh retains
the header and shows progress while patient actions are disabled.

401 recovery uses existing auth behavior: one coordinated refresh and one retry. If authentication
cannot recover, the login UI replaces the workspace and patient context is cleared. A 403 clears
results and active context and shows a localized access-denied message. Empty results are distinct
from errors. Network/server failures show a fixed localized message; the existing selected identity
can remain visible, but new clinical data is never inferred. Failed load-more can be retried without
skipping a page. Search cancellation is not displayed as an error.

## Files changed in this slice

- Added `apps/doctor_tablet/lib/features/patients/data/patient_search.dart`.
- Added `apps/doctor_tablet/lib/features/patients/state/patient_context_cubit.dart`.
- Added `apps/doctor_tablet/lib/features/patients/presentation/patient_workspace.dart`.
- Updated `apps/doctor_tablet/lib/app/doctor_tablet_app.dart` (provider/session routing).
- Updated `apps/doctor_tablet/lib/app/session/session_cubit.dart` (refresh retains staff identity).
- Updated `apps/doctor_tablet/lib/features/home/presentation/authenticated_shell.dart`.
- Updated both `apps/doctor_tablet/lib/l10n/app_*.arb` catalogs and the three generated localization files.
- Updated `apps/doctor_tablet/test/auth_fixture.dart`; added `apps/doctor_tablet/test/patient_context_test.dart`.
- Updated `apps/doctor_tablet/README.md` and added this report.

No packages added. No backend, migrations, `clinic_core`, notebook, drawing or offline clinical
storage changes were made in this slice. Prior working-tree changes were preserved.

## Limitations

Patient context is a last-fetched projection, not a live clinical monitor. Allergy status is the
server's recorded status, not a severity assessment. Re-search to get updated context. Backend
offset pagination is not a snapshot if records change concurrently. Tests use the real Dio/auth
client with deterministic adapters, not a live clinic or physical device. Existing HTTPS and
secure-storage configuration from Phase 1A-2C remains required.

## Verification

- `flutter analyze` in `apps/doctor_tablet`: no issues.
- `flutter test --timeout 60s`: all 38 tests passed, including 12 new patient-context cases.
- `git diff --check`: passed.
- `clinic_core` was not changed, so its checks were not rerun for this slice.

The new cases cover search success/empty results, bounds, load more, explicit selection/switching,
fixed patient header, all allergy indicators, 403 and network errors, logout and same-staff new
login isolation, refresh preservation/expiry cleanup, canceled and superseded requests, dismissal
of patient dialogs on session loss, and Arabic RTL/English LTR.
