# Doctor Tablet — Phase 1A-2C

Patient search and a session-scoped persistent patient header are now added in
[Phase 1A-3B](../../docs/doctor-tablet-patient-context-phase-1a-3b.md).
The authentication details below describe the preceding slice.

Arabic-first Android staff authentication, using the existing mobile backend endpoints.
No backend/migration, notebook, permissions, patient context, drawing or clinical storage changes.

## Run

Configure an explicit HTTPS origin with a certificate trusted by the device (no certificate bypass):

```sh
flutter pub get
flutter run --dart-define=CLINIC_API_URL=https://your-clinic-host
flutter analyze
flutter test
```

Missing/invalid configuration shows a localized configuration error and makes no credential request.
The old shared development HTTP URL is not used for authentication. Android needs API 23 or later.
The SDK on this workstation is `C:\src\flutter\bin`; add it to PATH before these commands.

## Authentication

`SessionCubit` owns the UI lifecycle: starting, unauthenticated, password loading, MFA required
(with verification progress/errors), authenticated, refreshing, logging out, and expired/error.
Credentials never appear in Cubit states. Password and TOTP controllers are cleared on submission
and disposed on navigation. MFA challenges stay in memory only. All auth errors use fixed localized
messages; server error bodies and Dio exceptions are never rendered or logged.

`MobileAuth` uses a plain Dio transport for login/MFA/refresh/logout and a protected Dio client with
an auth interceptor. It calls `GET /api/mobile/staff/auth/session` after MFA and on every startup
restoration. Only a validated session opens the protected shell. Restoration failure clears local
credentials, including when startup validation is unavailable offline.

`flutter_secure_storage` 11.2.0 is the only added direct dependency. The complete access/refresh
pair, expiry timestamps and HTTPS origin are serialized into ONE secure-storage value. Writes and
clears are serialized; the in-memory pair is published only after the write succeeds. This prevents
a mixed old/new pair from separate token writes. No SharedPreferences or file fallback exists.
Android uses the plugin's encrypted storage/Keystore defaults; backups and credential preference
transfer are disabled. iOS options select unlocked, this-device-only Keychain accessibility, but
this repository currently ships an Android platform project only.

## Refresh and logout

On protected-request 401, one shared in-flight refresh coordinates concurrent failures. A delayed
401 sent with the previous access token reuses the already-rotated credentials. Each original
request is retried once, with a retry marker and session generation guard. Another 401 on that
retry clears the session. Auth transport requests cannot recursively invoke the interceptor.

Refresh has NO automatic retry, including on timeouts: the server may already have consumed the
refresh token, and replay would revoke its family. Any refresh failure clears the local session
and routes back to sign-in. Refresh tokens are sent only in JSON bodies. Authorization headers
are attached only to the configured HTTPS origin; redirects are disabled. No request/response,
password, TOTP or credential logging is installed.

Logout blocks new protected requests, waits for an ongoing refresh, calls backend logout before
local cleanup, and clears credentials even if offline. If access expired, it may refresh once to
call logout with a valid access token. Offline logout cannot guarantee server-side revocation.

## Tests and limitations

The Dart tests use deterministic Dio adapters and injected storage. They exercise password/MFA,
invalid credentials/codes, startup restoration, expired access, concurrent and delayed 401s,
refresh failure/timeout, retry bounds, serialized pair replacement, storage failure, logout and
logout/refresh races, cross-origin rejection, secure-storage channel usage, no auth logs, and
Arabic/English UI behavior. These are not a live backend or physical-device Keystore test.

One coordinator is intended for one application isolate; multi-isolate/background refresh is not
supported. Platform storage errors clear memory and surface an error; a failed OS deletion cannot
guarantee physical removal. A process crash after server rotation but before local write may leave
an old pair and requires fresh sign-in. Arbitrary streamed/multipart request replay is outside this
slice (only JSON/GET auth requests are used). Token expiry and revocation remain server-authoritative.

Device binding, background cleanup, session UI, offline clinical data and all notebook work remain
out of scope. The shared `clinic_core` API contracts/configuration are reused without source changes.

## Changed files in Phase 1A-2C

All paths below are relative to `apps/doctor_tablet`:

- Added `lib/features/auth/data/mobile_auth.dart` and `token_store.dart`.
- Added `lib/features/auth/presentation/mfa_screen.dart` and `auth_widgets.dart`;
  replaced `login_screen.dart`.
- Updated `lib/app/session/session_cubit.dart`, `lib/app/doctor_tablet_app.dart`,
  `lib/core/api/api_client_factory.dart`, `lib/main.dart`, and
  `lib/features/home/presentation/authenticated_shell.dart`.
- Updated both `lib/l10n/app_*.arb` catalogs and three generated localization Dart files.
- Updated `pubspec.yaml`, `pubspec.lock`, and this README.
- Updated `android/app/build.gradle.kts` and `android/app/src/main/AndroidManifest.xml`;
  added `android/app/src/main/res/xml/backup_rules.xml` and `data_extraction_rules.xml`.
- Added `test/auth_fixture.dart`, `test/mobile_auth_test.dart`; replaced `test/app_shell_test.dart`.

Package reference: https://pub.dev/packages/flutter_secure_storage

## Verification results

- `apps/doctor_tablet`: `flutter pub get` succeeded; `flutter analyze` reported no issues;
  `flutter test --timeout 60s` passed all 26 tests.
- `packages/clinic_core`: `flutter pub get` succeeded; `flutter analyze` reported no issues;
  `flutter test` passed all 14 tests. No package source changes were required.
- `git diff --check` passed. Backend and migration files were unchanged in this slice.
- Physical-device secure storage, a live HTTPS backend, and APK/release signing were not validated.
