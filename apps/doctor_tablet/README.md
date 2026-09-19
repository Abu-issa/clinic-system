# doctor_tablet

Flutter tablet workspace for Clinic staff (Android tablet, Arabic-first).

This slice (Phase 1A-1) is the **project foundation only**:

- Feature-based structure (`lib/features/<feature>/presentation`)
- Arabic (default) + English localization with automatic RTL/LTR (`lib/l10n`, `l10n.yaml`)
- Placeholder session flow: startup screen → login placeholder → authenticated shell
  (`lib/app/session/session_cubit.dart`) — real MFA authentication is a later slice
- `clinic_core` supplies API configuration and transport-agnostic contracts

Not yet implemented: authentication/MFA, tokens, notebook, ink, storage, offline drafts.

## Commands

```sh
flutter pub get
flutter analyze
flutter test
```

Backend base URL for local runs is configured in `clinic_core` (`ApiConfig.developmentBaseUrl`,
Android emulator loopback `10.0.2.2`).
