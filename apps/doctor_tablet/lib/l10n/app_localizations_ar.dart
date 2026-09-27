// ignore: unused_import
import 'package:intl/intl.dart' as intl;

import 'app_localizations.dart';

// ignore_for_file: type=lint

/// The translations for Arabic (`ar`).
class AppLocalizationsAr extends AppLocalizations {
  AppLocalizationsAr([String locale = 'ar']) : super(locale);

  @override
  String get notebookPages => 'صفحات الدفتر';

  @override
  String get notebookPrevious => 'الصفحة السابقة';

  @override
  String get notebookNext => 'الصفحة التالية';

  @override
  String notebookPosition(String position, String count) {
    return 'الصفحة $position من $count';
  }

  @override
  String get conflictConfirmAction => 'تأكيد تجاهل تعديلاتي';

  @override
  String get conflictConfirmDiscard =>
      'سيتم حذف الحبر غير المتزامن والمراجعات المعلقة لهذه الصفحة بعد قراءة أحدث حبر من الخادم بنجاح. لا يمكن التراجع عن ذلك.';

  @override
  String get conflictDetected => 'تم اكتشاف تعارض';

  @override
  String get conflictDiscard => 'تجاهل تعديلاتي';

  @override
  String get conflictExplanation =>
      'لن يتم دمج الحبر تلقائياً. حفظ تعديلاتي يحتفظ بالحبر المحلي كمراجعة جديدة؛ وتستخدم الصفحات النهائية ملحق تعديل.';

  @override
  String get conflictLater => 'اتخاذ القرار لاحقاً';

  @override
  String get conflictLocalSaved => 'آخر حفظ محلي';

  @override
  String get conflictQueuedCount => 'المراجعات المحلية المعلقة';

  @override
  String get conflictResolutionFailed =>
      'فشلت معالجة التعارض. تبقى الصفحة في حالة تعارض وتبقى تعديلاتك المحلية محفوظة.';

  @override
  String get conflictResolved => 'تمت معالجة التعارض بنجاح';

  @override
  String get conflictSaveNew => 'حفظ تعديلاتي كمراجعة جديدة';

  @override
  String get conflictServerRevision => 'مراجعة الخادم المعروفة';

  @override
  String get conflictServerUpdated => 'وقت تحديث الخادم المعروف';

  @override
  String get conflictUnknown => 'غير متاح';

  @override
  String get inkQueued => 'في قائمة المزامنة';

  @override
  String get inkSyncFailed => 'فشلت المزامنة';

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

  @override
  String get notebookConflict =>
      'يتعارض الطلب مع حالة الصفحة. حدّث قبل المتابعة.';

  @override
  String get notebookFoundation =>
      'دفتر حبر متجهي. يتطلب الاعتماد مزامنة الحبر.';

  @override
  String get notebookTitleRequired => 'أدخل عنواناً من ١ إلى ٢٠٠ حرف.';

  @override
  String get notebookFinalize => 'اعتماد الصفحة كنهائية';

  @override
  String get notebookFailed =>
      'تعذر إكمال الطلب. حدّث للتحقق من حالة الخادم قبل إنشاء صفحة أخرى.';

  @override
  String get notebookRetry => 'إعادة إرسال الطلب نفسه';

  @override
  String get notebookRowVersion => 'إصدار الصف';

  @override
  String get notebookRevision => 'المراجعة';

  @override
  String get notebookFinalized => 'نهائية';

  @override
  String get notebookDraft => 'مسودة';

  @override
  String get notebookCreate => 'إنشاء صفحة';

  @override
  String get notebookChanged =>
      'تغيّرت الصفحة على الخادم. حُفظت حالتك الحالية. حدّث الصفحة قبل المتابعة.';

  @override
  String get notebookTitle => 'دفتر الملاحظات';

  @override
  String get notebookForbidden => 'غير مصرح بالوصول إلى دفتر الملاحظات.';

  @override
  String get notebookAmend => 'إضافة ملحق';

  @override
  String get notebookPageTitle => 'عنوان الصفحة';

  @override
  String get notebookSubmit => 'مزامنة نسخة الحبر';

  @override
  String get notebookEmpty => 'لا توجد صفحات بعد.';

  @override
  String get notebookCreated => 'أُنشئت';

  @override
  String get notebookRetryHint =>
      'نتيجة الطلب غير مؤكدة. أعد إرسال الطلب نفسه لتجنب التكرار، أو حدّث للاطلاع على حالة الخادم.';

  @override
  String get notebookLoadMore => 'تحميل المزيد من الصفحات';

  @override
  String get notebookUpdated => 'آخر تحديث';

  @override
  String get notebookRefresh => 'تحديث من الخادم';

  @override
  String get inkBlack => 'أسود';

  @override
  String get inkBlue => 'أزرق';

  @override
  String get inkBold => 'عريض';

  @override
  String get inkEraser => 'ممحاة ضربة كاملة';

  @override
  String get inkFine => 'رفيع';

  @override
  String get inkMedium => 'متوسط';

  @override
  String get inkPen => 'قلم';

  @override
  String get inkRed => 'أحمر';

  @override
  String get inkRedo => 'إعادة';

  @override
  String get inkTemporary =>
      'الحبر مؤقت — لا يُحفظ ولا يُرفع. مغادرة الصفحة أو تغيير المريض يمسح الحبر.';

  @override
  String get inkUndo => 'تراجع';

  @override
  String get inkNavigation => 'إصبع واحد: تحريك · إصبعان: تكبير وتصغير';

  @override
  String get inkResetView => 'إعادة ضبط العرض';

  @override
  String get inkStylusActive => 'القلم نشط · التنقل بالأصابع متوقف';

  @override
  String get inkLoading => 'جارٍ فتح المسودة المحلية المشفرة…';

  @override
  String get inkLocalChanges => 'تغييرات محلية';

  @override
  String get inkLocalNotice =>
      'حفظ محلي مشفر تلقائي. استخدم المزامنة لحفظ الحبر على الخادم.';

  @override
  String get inkLocalSaved => 'محفوظ محلياً';

  @override
  String get inkLogoutBlocked =>
      'تم إيقاف تسجيل الخروج بسبب فشل تخزين المسودة محلياً. أبقِ التطبيق مفتوحاً وأعد محاولة الحفظ قبل الخروج.';

  @override
  String get inkRestoreFailed =>
      'تعذر فتح المسودة المحلية بأمان. الحبر مخفي والتحرير معطل. أعد المحاولة أو اتصل بالدعم؛ لا تمسح بيانات التطبيق.';

  @override
  String get inkRetrySave => 'إعادة محاولة التخزين المحلي';

  @override
  String get inkSaveFailed =>
      'فشل الحفظ المحلي. التغييرات باقية في الذاكرة. أبقِ التطبيق مفتوحاً وأعد المحاولة.';

  @override
  String get inkBeginAmendment => 'بدء تعديل الحبر';

  @override
  String get inkConflict => 'تعارض';

  @override
  String get inkOffline => 'غير متصل — محفوظ محلياً';

  @override
  String get inkServerSynced => 'تمت المزامنة مع الخادم';

  @override
  String get inkSyncing => 'جارٍ المزامنة';
}
