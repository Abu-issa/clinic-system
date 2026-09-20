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
  String get shellTitle => 'المساحة المحمية';

  @override
  String get shellPlaceholderNote =>
      'هيكل مؤقت — دفتر الملاحظات والخطوات السريرية تُبنى في المراحل القادمة.';

  @override
  String get shellSignOut => 'خروج';

  @override
  String get languageToggle => 'English';

  @override
  String get authStorageError =>
      'التخزين الآمن غير متاح. يرجى المحاولة مجددًا.';

  @override
  String get signingIn => 'جارٍ تسجيل الدخول…';

  @override
  String get mfaNote => 'أدخل الرمز المكوّن من ستة أرقام من تطبيق المصادقة.';

  @override
  String get requiredField => 'هذا الحقل مطلوب.';

  @override
  String get invalidMfa =>
      'الرمز أو طلب التحقق غير صالح. حاول مجددًا أو أعد تسجيل الدخول.';

  @override
  String get enrollmentRequired =>
      'أكمل إعداد المصادقة متعددة العوامل في موقع الطاقم أولًا.';

  @override
  String get mfaTitle => 'التحقق بخطوتين';

  @override
  String get loginPassword => 'كلمة المرور';

  @override
  String get authConfigurationError =>
      'يجب إعداد عنوان HTTPS موثوق لواجهة العيادة.';

  @override
  String get sessionExpired => 'انتهت جلستك. يرجى تسجيل الدخول مجددًا.';

  @override
  String get verifyingMfa => 'جارٍ التحقق…';

  @override
  String get verifyMfa => 'تحقق';

  @override
  String get mfaCodeRequired => 'أدخل رمزًا مكوّنًا من ستة أرقام.';

  @override
  String get loginNote =>
      'استخدم حساب الطاقم، ثم أدخل الرمز من تطبيق المصادقة.';

  @override
  String get backToLogin => 'العودة لتسجيل الدخول';

  @override
  String get signIn => 'تسجيل الدخول';

  @override
  String get loginIdentifier => 'اسم المستخدم أو البريد الإلكتروني';

  @override
  String get authNetworkError => 'تعذّر الاتصال. تحقق من اتصالك وحاول مجددًا.';

  @override
  String get invalidCredentials =>
      'تعذّر تسجيل الدخول. تحقق من بياناتك وحاول مجددًا.';

  @override
  String get mfaCode => 'رمز المصادقة';

  @override
  String get patientActive => 'المريض الحالي';

  @override
  String get patientDob => 'تاريخ الميلاد';

  @override
  String get patientSwitchCancel => 'الإبقاء على المريض الحالي';

  @override
  String get patientSearchEmpty => 'لا يوجد مرضى مطابقون ضمن نطاق صلاحياتك.';

  @override
  String get patientMrn => 'رقم الملف الطبي';

  @override
  String get patientNotRecorded => 'غير مسجل';

  @override
  String get patientSearchTerm => 'الاسم أو رقم الملف الطبي أو الورقي';

  @override
  String patientSwitchMessage(String nextName, String currentName) {
    return 'هل تريد استبدال $currentName بالمريض $nextName كمريض حالي؟';
  }

  @override
  String get patientSelect => 'اختيار';

  @override
  String get patientAllergyNone => 'لا توجد حساسية معروفة';

  @override
  String get patientForbidden => 'ليس لديك صلاحية البحث عن المرضى.';

  @override
  String get patientNetworkError =>
      'البحث غير متاح. تحقق من اتصالك وحاول مجددًا.';

  @override
  String get patientSelected => 'تم الاختيار';

  @override
  String get patientSwitchConfirm => 'تغيير المريض';

  @override
  String get patientSearchHint => 'ابحث لاختيار المريض.';

  @override
  String get patientSearchAction => 'بحث';

  @override
  String get patientLoadMore => 'تحميل المزيد';

  @override
  String get patientAllergyKnown => 'توجد حساسية مسجلة';

  @override
  String get patientInvalidTerm => 'أدخل من حرفين إلى ١٠٠ حرف دون محارف تحكم.';

  @override
  String get patientAllergyUnknown => 'حالة الحساسية غير معروفة';

  @override
  String get patientSwitchTitle => 'تغيير المريض الحالي؟';

  @override
  String get patientSearchTitle => 'البحث عن مريض';
}
