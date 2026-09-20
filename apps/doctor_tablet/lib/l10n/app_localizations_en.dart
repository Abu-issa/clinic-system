// ignore: unused_import
import 'package:intl/intl.dart' as intl;

import 'app_localizations.dart';

// ignore_for_file: type=lint

/// The translations for English (`en`).
class AppLocalizationsEn extends AppLocalizations {
  AppLocalizationsEn([String locale = 'en']) : super(locale);

  @override
  String get appTitle => 'Doctor Workspace';

  @override
  String get startupTagline => 'Clinical notebook and workflows';

  @override
  String get startupInitializing => 'Initializing…';

  @override
  String get loginTitle => 'Staff sign-in';

  @override
  String get shellTitle => 'Protected workspace';

  @override
  String get shellPlaceholderNote =>
      'Placeholder shell — the notebook and clinical steps are built in later phases.';

  @override
  String get shellSignOut => 'Sign out';

  @override
  String get languageToggle => 'العربية';

  @override
  String get authStorageError =>
      'Secure storage is unavailable. Please try again.';

  @override
  String get signingIn => 'Signing in…';

  @override
  String get mfaNote => 'Enter the six-digit code from your authenticator app.';

  @override
  String get requiredField => 'This field is required.';

  @override
  String get invalidMfa =>
      'The code or challenge is invalid. Try again or restart sign-in.';

  @override
  String get enrollmentRequired =>
      'Complete MFA enrollment in the staff web application first.';

  @override
  String get mfaTitle => 'Two-step verification';

  @override
  String get loginPassword => 'Password';

  @override
  String get authConfigurationError =>
      'A trusted HTTPS clinic API address must be configured.';

  @override
  String get sessionExpired => 'Your session ended. Please sign in again.';

  @override
  String get verifyingMfa => 'Verifying…';

  @override
  String get verifyMfa => 'Verify';

  @override
  String get mfaCodeRequired => 'Enter a six-digit code.';

  @override
  String get loginNote =>
      'Use your staff account, then enter your authenticator code.';

  @override
  String get backToLogin => 'Back to sign-in';

  @override
  String get signIn => 'Sign in';

  @override
  String get loginIdentifier => 'Username or email';

  @override
  String get authNetworkError =>
      'Unable to connect. Check your connection and try again.';

  @override
  String get invalidCredentials =>
      'Unable to sign in. Check your credentials and try again.';

  @override
  String get mfaCode => 'Authenticator code';

  @override
  String get patientActive => 'Active patient';

  @override
  String get patientDob => 'Date of birth';

  @override
  String get patientSwitchCancel => 'Keep current patient';

  @override
  String get patientSearchEmpty =>
      'No matching patients in your permitted scope.';

  @override
  String get patientMrn => 'MRN';

  @override
  String get patientNotRecorded => 'Not recorded';

  @override
  String get patientSearchTerm => 'Name, MRN or paper file number';

  @override
  String patientSwitchMessage(String nextName, String currentName) {
    return 'Replace $currentName with $nextName as the active patient?';
  }

  @override
  String get patientSelect => 'Select';

  @override
  String get patientAllergyNone => 'No known allergies';

  @override
  String get patientForbidden =>
      'You do not have access to this patient search.';

  @override
  String get patientNetworkError =>
      'Search unavailable. Check your connection and try again.';

  @override
  String get patientSelected => 'Selected';

  @override
  String get patientSwitchConfirm => 'Switch patient';

  @override
  String get patientSearchHint => 'Search to select a patient.';

  @override
  String get patientSearchAction => 'Search';

  @override
  String get patientLoadMore => 'Load more';

  @override
  String get patientAllergyKnown => 'Known allergies recorded';

  @override
  String get patientInvalidTerm =>
      'Enter 2–100 characters without control characters.';

  @override
  String get patientAllergyUnknown => 'Allergy status unknown';

  @override
  String get patientSwitchTitle => 'Switch active patient?';

  @override
  String get patientSearchTitle => 'Find a patient';
}
