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
  String get loginPlaceholderNote =>
      'Placeholder screen — real MFA authentication arrives in a later phase.';

  @override
  String get loginContinuePlaceholder => 'Continue (placeholder)';

  @override
  String get shellTitle => 'Protected workspace';

  @override
  String get shellPlaceholderNote =>
      'Placeholder shell — the notebook and clinical steps are built in later phases.';

  @override
  String get shellSignOut => 'Sign out';

  @override
  String get languageToggle => 'العربية';
}
