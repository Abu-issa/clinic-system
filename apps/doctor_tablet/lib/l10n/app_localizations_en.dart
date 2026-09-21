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

  @override
  String get notebookConflict =>
      'The submission conflicts with the page state. Refresh before continuing.';

  @override
  String get notebookFoundation =>
      'Foundation actions submit metadata only. Ink is temporary and is not included in submissions.';

  @override
  String get notebookTitleRequired => 'Enter a title of 1–200 characters.';

  @override
  String get notebookFinalize => 'Finalize page';

  @override
  String get notebookFailed =>
      'The request could not be completed. Refresh to check server state before creating another page.';

  @override
  String get notebookRetry => 'Retry same submission';

  @override
  String get notebookRowVersion => 'RowVersion';

  @override
  String get notebookRevision => 'Revision';

  @override
  String get notebookFinalized => 'Finalized';

  @override
  String get notebookDraft => 'Draft';

  @override
  String get notebookCreate => 'Create page';

  @override
  String get notebookChanged =>
      'This page changed on the server. Your current state is preserved. Refresh explicitly before continuing.';

  @override
  String get notebookTitle => 'Notebook';

  @override
  String get notebookForbidden => 'Notebook access is not authorized.';

  @override
  String get notebookAmend => 'Add amendment';

  @override
  String get notebookPageTitle => 'Page title';

  @override
  String get notebookSubmit => 'Submit minimal revision';

  @override
  String get notebookEmpty => 'No notebook pages yet.';

  @override
  String get notebookCreated => 'Created';

  @override
  String get notebookRetryHint =>
      'The outcome is uncertain. Retry the same submission to avoid duplicates, or refresh to inspect the server state.';

  @override
  String get notebookLoadMore => 'Load more pages';

  @override
  String get notebookUpdated => 'Updated';

  @override
  String get notebookRefresh => 'Refresh from server';

  @override
  String get inkBlack => 'Black';

  @override
  String get inkBlue => 'Blue';

  @override
  String get inkBold => 'Bold';

  @override
  String get inkEraser => 'Whole-stroke eraser';

  @override
  String get inkFine => 'Fine';

  @override
  String get inkMedium => 'Medium';

  @override
  String get inkPen => 'Pen';

  @override
  String get inkRed => 'Red';

  @override
  String get inkRedo => 'Redo';

  @override
  String get inkTemporary =>
      'Temporary ink — not saved or uploaded. Leaving this page or switching patient discards ink.';

  @override
  String get inkUndo => 'Undo';

  @override
  String get inkNavigation => 'One finger: pan · Two fingers: zoom';

  @override
  String get inkResetView => 'Reset view';

  @override
  String get inkStylusActive => 'Stylus active · Finger navigation paused';
}
