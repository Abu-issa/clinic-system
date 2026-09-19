// ignore: unused_import
import 'package:intl/intl.dart' as intl;

import 'app_localizations.dart';

// ignore_for_file: type=lint

/// The translations for Arabic (`ar`).
class AppLocalizationsAr extends AppLocalizations {
  AppLocalizationsAr([String locale = 'ar']) : super(locale);

  @override
  String get appTitle => 'مساحة الطبيب';

  @override
  String get startupTagline => 'دفتر الملاحظات الطبي وسير العمل السريري';

  @override
  String get startupInitializing => 'جارٍ التهيئة…';

  @override
  String get loginTitle => 'تسجيل دخول الطاقم الطبي';

  @override
  String get loginPlaceholderNote =>
      'شاشة مؤقتة — المصادقة الحقيقية متعددة العوامل تُنفَّذ في مرحلة لاحقة.';

  @override
  String get loginContinuePlaceholder => 'متابعة (مؤقت)';

  @override
  String get shellTitle => 'المساحة المحمية';

  @override
  String get shellPlaceholderNote =>
      'هيكل مؤقت — دفتر الملاحظات والخطوات السريرية تُبنى في المراحل القادمة.';

  @override
  String get shellSignOut => 'خروج';

  @override
  String get languageToggle => 'English';
}
