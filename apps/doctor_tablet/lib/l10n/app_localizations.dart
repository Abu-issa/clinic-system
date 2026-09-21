import 'dart:async';

import 'package:flutter/foundation.dart';
import 'package:flutter/widgets.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:intl/intl.dart' as intl;

import 'app_localizations_ar.dart';
import 'app_localizations_en.dart';

// ignore_for_file: type=lint

/// Callers can lookup localized strings with an instance of AppLocalizations
/// returned by `AppLocalizations.of(context)`.
///
/// Applications need to include `AppLocalizations.delegate()` in their app's
/// `localizationDelegates` list, and the locales they support in the app's
/// `supportedLocales` list. For example:
///
/// ```dart
/// import 'l10n/app_localizations.dart';
///
/// return MaterialApp(
///   localizationsDelegates: AppLocalizations.localizationsDelegates,
///   supportedLocales: AppLocalizations.supportedLocales,
///   home: MyApplicationHome(),
/// );
/// ```
///
/// ## Update pubspec.yaml
///
/// Please make sure to update your pubspec.yaml to include the following
/// packages:
///
/// ```yaml
/// dependencies:
///   # Internationalization support.
///   flutter_localizations:
///     sdk: flutter
///   intl: any # Use the pinned version from flutter_localizations
///
///   # Rest of dependencies
/// ```
///
/// ## iOS Applications
///
/// iOS applications define key application metadata, including supported
/// locales, in an Info.plist file that is built into the application bundle.
/// To configure the locales supported by your app, you’ll need to edit this
/// file.
///
/// First, open your project’s ios/Runner.xcworkspace Xcode workspace file.
/// Then, in the Project Navigator, open the Info.plist file under the Runner
/// project’s Runner folder.
///
/// Next, select the Information Property List item, select Add Item from the
/// Editor menu, then select Localizations from the pop-up menu.
///
/// Select and expand the newly-created Localizations item then, for each
/// locale your application supports, add a new item and select the locale
/// you wish to add from the pop-up menu in the Value field. This list should
/// be consistent with the languages listed in the AppLocalizations.supportedLocales
/// property.
abstract class AppLocalizations {
  AppLocalizations(String locale)
    : localeName = intl.Intl.canonicalizedLocale(locale.toString());

  final String localeName;

  static AppLocalizations of(BuildContext context) {
    return Localizations.of<AppLocalizations>(context, AppLocalizations)!;
  }

  static const LocalizationsDelegate<AppLocalizations> delegate =
      _AppLocalizationsDelegate();

  /// A list of this localizations delegate along with the default localizations
  /// delegates.
  ///
  /// Returns a list of localizations delegates containing this delegate along with
  /// GlobalMaterialLocalizations.delegate, GlobalCupertinoLocalizations.delegate,
  /// and GlobalWidgetsLocalizations.delegate.
  ///
  /// Additional delegates can be added by appending to this list in
  /// MaterialApp. This list does not have to be used at all if a custom list
  /// of delegates is preferred or required.
  static const List<LocalizationsDelegate<dynamic>> localizationsDelegates =
      <LocalizationsDelegate<dynamic>>[
        delegate,
        GlobalMaterialLocalizations.delegate,
        GlobalCupertinoLocalizations.delegate,
        GlobalWidgetsLocalizations.delegate,
      ];

  /// A list of this localizations delegate's supported locales.
  static const List<Locale> supportedLocales = <Locale>[
    Locale('ar'),
    Locale('en'),
  ];

  /// Application name shown in the app bar and window title.
  ///
  /// In ar, this message translates to:
  /// **'مساحة الطبيب'**
  String get appTitle;

  /// Short description under the logo on the startup screen.
  ///
  /// In ar, this message translates to:
  /// **'دفتر الملاحظات الطبي وسير العمل السريري'**
  String get startupTagline;

  /// Progress label while the app initializes.
  ///
  /// In ar, this message translates to:
  /// **'جارٍ التهيئة…'**
  String get startupInitializing;

  /// Staff login screen title.
  ///
  /// In ar, this message translates to:
  /// **'تسجيل دخول الطاقم الطبي'**
  String get loginTitle;

  /// Title of the placeholder authenticated shell.
  ///
  /// In ar, this message translates to:
  /// **'المساحة المحمية'**
  String get shellTitle;

  /// Notice that the authenticated shell is a placeholder.
  ///
  /// In ar, this message translates to:
  /// **'هيكل مؤقت — دفتر الملاحظات والخطوات السريرية تُبنى في المراحل القادمة.'**
  String get shellPlaceholderNote;

  /// Sign-out action in the authenticated shell.
  ///
  /// In ar, this message translates to:
  /// **'خروج'**
  String get shellSignOut;

  /// Label of the language switch button; shows the other language's name.
  ///
  /// In ar, this message translates to:
  /// **'English'**
  String get languageToggle;

  /// No description provided for @authStorageError.
  ///
  /// In ar, this message translates to:
  /// **'التخزين الآمن غير متاح. يرجى المحاولة مجددًا.'**
  String get authStorageError;

  /// No description provided for @signingIn.
  ///
  /// In ar, this message translates to:
  /// **'جارٍ تسجيل الدخول…'**
  String get signingIn;

  /// No description provided for @mfaNote.
  ///
  /// In ar, this message translates to:
  /// **'أدخل الرمز المكوّن من ستة أرقام من تطبيق المصادقة.'**
  String get mfaNote;

  /// No description provided for @requiredField.
  ///
  /// In ar, this message translates to:
  /// **'هذا الحقل مطلوب.'**
  String get requiredField;

  /// No description provided for @invalidMfa.
  ///
  /// In ar, this message translates to:
  /// **'الرمز أو طلب التحقق غير صالح. حاول مجددًا أو أعد تسجيل الدخول.'**
  String get invalidMfa;

  /// No description provided for @enrollmentRequired.
  ///
  /// In ar, this message translates to:
  /// **'أكمل إعداد المصادقة متعددة العوامل في موقع الطاقم أولًا.'**
  String get enrollmentRequired;

  /// No description provided for @mfaTitle.
  ///
  /// In ar, this message translates to:
  /// **'التحقق بخطوتين'**
  String get mfaTitle;

  /// No description provided for @loginPassword.
  ///
  /// In ar, this message translates to:
  /// **'كلمة المرور'**
  String get loginPassword;

  /// No description provided for @authConfigurationError.
  ///
  /// In ar, this message translates to:
  /// **'يجب إعداد عنوان HTTPS موثوق لواجهة العيادة.'**
  String get authConfigurationError;

  /// No description provided for @sessionExpired.
  ///
  /// In ar, this message translates to:
  /// **'انتهت جلستك. يرجى تسجيل الدخول مجددًا.'**
  String get sessionExpired;

  /// No description provided for @verifyingMfa.
  ///
  /// In ar, this message translates to:
  /// **'جارٍ التحقق…'**
  String get verifyingMfa;

  /// No description provided for @verifyMfa.
  ///
  /// In ar, this message translates to:
  /// **'تحقق'**
  String get verifyMfa;

  /// No description provided for @mfaCodeRequired.
  ///
  /// In ar, this message translates to:
  /// **'أدخل رمزًا مكوّنًا من ستة أرقام.'**
  String get mfaCodeRequired;

  /// No description provided for @loginNote.
  ///
  /// In ar, this message translates to:
  /// **'استخدم حساب الطاقم، ثم أدخل الرمز من تطبيق المصادقة.'**
  String get loginNote;

  /// No description provided for @backToLogin.
  ///
  /// In ar, this message translates to:
  /// **'العودة لتسجيل الدخول'**
  String get backToLogin;

  /// No description provided for @signIn.
  ///
  /// In ar, this message translates to:
  /// **'تسجيل الدخول'**
  String get signIn;

  /// No description provided for @loginIdentifier.
  ///
  /// In ar, this message translates to:
  /// **'اسم المستخدم أو البريد الإلكتروني'**
  String get loginIdentifier;

  /// No description provided for @authNetworkError.
  ///
  /// In ar, this message translates to:
  /// **'تعذّر الاتصال. تحقق من اتصالك وحاول مجددًا.'**
  String get authNetworkError;

  /// No description provided for @invalidCredentials.
  ///
  /// In ar, this message translates to:
  /// **'تعذّر تسجيل الدخول. تحقق من بياناتك وحاول مجددًا.'**
  String get invalidCredentials;

  /// No description provided for @mfaCode.
  ///
  /// In ar, this message translates to:
  /// **'رمز المصادقة'**
  String get mfaCode;

  /// No description provided for @patientActive.
  ///
  /// In ar, this message translates to:
  /// **'المريض الحالي'**
  String get patientActive;

  /// No description provided for @patientDob.
  ///
  /// In ar, this message translates to:
  /// **'تاريخ الميلاد'**
  String get patientDob;

  /// No description provided for @patientSwitchCancel.
  ///
  /// In ar, this message translates to:
  /// **'الإبقاء على المريض الحالي'**
  String get patientSwitchCancel;

  /// No description provided for @patientSearchEmpty.
  ///
  /// In ar, this message translates to:
  /// **'لا يوجد مرضى مطابقون ضمن نطاق صلاحياتك.'**
  String get patientSearchEmpty;

  /// No description provided for @patientMrn.
  ///
  /// In ar, this message translates to:
  /// **'رقم الملف الطبي'**
  String get patientMrn;

  /// No description provided for @patientNotRecorded.
  ///
  /// In ar, this message translates to:
  /// **'غير مسجل'**
  String get patientNotRecorded;

  /// No description provided for @patientSearchTerm.
  ///
  /// In ar, this message translates to:
  /// **'الاسم أو رقم الملف الطبي أو الورقي'**
  String get patientSearchTerm;

  /// No description provided for @patientSwitchMessage.
  ///
  /// In ar, this message translates to:
  /// **'هل تريد استبدال {currentName} بالمريض {nextName} كمريض حالي؟'**
  String patientSwitchMessage(String nextName, String currentName);

  /// No description provided for @patientSelect.
  ///
  /// In ar, this message translates to:
  /// **'اختيار'**
  String get patientSelect;

  /// No description provided for @patientAllergyNone.
  ///
  /// In ar, this message translates to:
  /// **'لا توجد حساسية معروفة'**
  String get patientAllergyNone;

  /// No description provided for @patientForbidden.
  ///
  /// In ar, this message translates to:
  /// **'ليس لديك صلاحية البحث عن المرضى.'**
  String get patientForbidden;

  /// No description provided for @patientNetworkError.
  ///
  /// In ar, this message translates to:
  /// **'البحث غير متاح. تحقق من اتصالك وحاول مجددًا.'**
  String get patientNetworkError;

  /// No description provided for @patientSelected.
  ///
  /// In ar, this message translates to:
  /// **'تم الاختيار'**
  String get patientSelected;

  /// No description provided for @patientSwitchConfirm.
  ///
  /// In ar, this message translates to:
  /// **'تغيير المريض'**
  String get patientSwitchConfirm;

  /// No description provided for @patientSearchHint.
  ///
  /// In ar, this message translates to:
  /// **'ابحث لاختيار المريض.'**
  String get patientSearchHint;

  /// No description provided for @patientSearchAction.
  ///
  /// In ar, this message translates to:
  /// **'بحث'**
  String get patientSearchAction;

  /// No description provided for @patientLoadMore.
  ///
  /// In ar, this message translates to:
  /// **'تحميل المزيد'**
  String get patientLoadMore;

  /// No description provided for @patientAllergyKnown.
  ///
  /// In ar, this message translates to:
  /// **'توجد حساسية مسجلة'**
  String get patientAllergyKnown;

  /// No description provided for @patientInvalidTerm.
  ///
  /// In ar, this message translates to:
  /// **'أدخل من حرفين إلى ١٠٠ حرف دون محارف تحكم.'**
  String get patientInvalidTerm;

  /// No description provided for @patientAllergyUnknown.
  ///
  /// In ar, this message translates to:
  /// **'حالة الحساسية غير معروفة'**
  String get patientAllergyUnknown;

  /// No description provided for @patientSwitchTitle.
  ///
  /// In ar, this message translates to:
  /// **'تغيير المريض الحالي؟'**
  String get patientSwitchTitle;

  /// No description provided for @patientSearchTitle.
  ///
  /// In ar, this message translates to:
  /// **'البحث عن مريض'**
  String get patientSearchTitle;

  /// No description provided for @notebookConflict.
  ///
  /// In ar, this message translates to:
  /// **'يتعارض الطلب مع حالة الصفحة. حدّث قبل المتابعة.'**
  String get notebookConflict;

  /// No description provided for @notebookFoundation.
  ///
  /// In ar, this message translates to:
  /// **'ترسل إجراءات التأسيس البيانات الوصفية فقط. الحبر مؤقت ولا يُضمّن في الإرسال.'**
  String get notebookFoundation;

  /// No description provided for @notebookTitleRequired.
  ///
  /// In ar, this message translates to:
  /// **'أدخل عنواناً من ١ إلى ٢٠٠ حرف.'**
  String get notebookTitleRequired;

  /// No description provided for @notebookFinalize.
  ///
  /// In ar, this message translates to:
  /// **'اعتماد الصفحة كنهائية'**
  String get notebookFinalize;

  /// No description provided for @notebookFailed.
  ///
  /// In ar, this message translates to:
  /// **'تعذر إكمال الطلب. حدّث للتحقق من حالة الخادم قبل إنشاء صفحة أخرى.'**
  String get notebookFailed;

  /// No description provided for @notebookRetry.
  ///
  /// In ar, this message translates to:
  /// **'إعادة إرسال الطلب نفسه'**
  String get notebookRetry;

  /// No description provided for @notebookRowVersion.
  ///
  /// In ar, this message translates to:
  /// **'إصدار الصف'**
  String get notebookRowVersion;

  /// No description provided for @notebookRevision.
  ///
  /// In ar, this message translates to:
  /// **'المراجعة'**
  String get notebookRevision;

  /// No description provided for @notebookFinalized.
  ///
  /// In ar, this message translates to:
  /// **'نهائية'**
  String get notebookFinalized;

  /// No description provided for @notebookDraft.
  ///
  /// In ar, this message translates to:
  /// **'مسودة'**
  String get notebookDraft;

  /// No description provided for @notebookCreate.
  ///
  /// In ar, this message translates to:
  /// **'إنشاء صفحة'**
  String get notebookCreate;

  /// No description provided for @notebookChanged.
  ///
  /// In ar, this message translates to:
  /// **'تغيّرت الصفحة على الخادم. حُفظت حالتك الحالية. حدّث الصفحة قبل المتابعة.'**
  String get notebookChanged;

  /// No description provided for @notebookTitle.
  ///
  /// In ar, this message translates to:
  /// **'دفتر الملاحظات'**
  String get notebookTitle;

  /// No description provided for @notebookForbidden.
  ///
  /// In ar, this message translates to:
  /// **'غير مصرح بالوصول إلى دفتر الملاحظات.'**
  String get notebookForbidden;

  /// No description provided for @notebookAmend.
  ///
  /// In ar, this message translates to:
  /// **'إضافة ملحق'**
  String get notebookAmend;

  /// No description provided for @notebookPageTitle.
  ///
  /// In ar, this message translates to:
  /// **'عنوان الصفحة'**
  String get notebookPageTitle;

  /// No description provided for @notebookSubmit.
  ///
  /// In ar, this message translates to:
  /// **'إرسال مراجعة أولية'**
  String get notebookSubmit;

  /// No description provided for @notebookEmpty.
  ///
  /// In ar, this message translates to:
  /// **'لا توجد صفحات بعد.'**
  String get notebookEmpty;

  /// No description provided for @notebookCreated.
  ///
  /// In ar, this message translates to:
  /// **'أُنشئت'**
  String get notebookCreated;

  /// No description provided for @notebookRetryHint.
  ///
  /// In ar, this message translates to:
  /// **'نتيجة الطلب غير مؤكدة. أعد إرسال الطلب نفسه لتجنب التكرار، أو حدّث للاطلاع على حالة الخادم.'**
  String get notebookRetryHint;

  /// No description provided for @notebookLoadMore.
  ///
  /// In ar, this message translates to:
  /// **'تحميل المزيد من الصفحات'**
  String get notebookLoadMore;

  /// No description provided for @notebookUpdated.
  ///
  /// In ar, this message translates to:
  /// **'آخر تحديث'**
  String get notebookUpdated;

  /// No description provided for @notebookRefresh.
  ///
  /// In ar, this message translates to:
  /// **'تحديث من الخادم'**
  String get notebookRefresh;

  /// No description provided for @inkBlack.
  ///
  /// In ar, this message translates to:
  /// **'أسود'**
  String get inkBlack;

  /// No description provided for @inkBlue.
  ///
  /// In ar, this message translates to:
  /// **'أزرق'**
  String get inkBlue;

  /// No description provided for @inkBold.
  ///
  /// In ar, this message translates to:
  /// **'عريض'**
  String get inkBold;

  /// No description provided for @inkEraser.
  ///
  /// In ar, this message translates to:
  /// **'ممحاة ضربة كاملة'**
  String get inkEraser;

  /// No description provided for @inkFine.
  ///
  /// In ar, this message translates to:
  /// **'رفيع'**
  String get inkFine;

  /// No description provided for @inkMedium.
  ///
  /// In ar, this message translates to:
  /// **'متوسط'**
  String get inkMedium;

  /// No description provided for @inkPen.
  ///
  /// In ar, this message translates to:
  /// **'قلم'**
  String get inkPen;

  /// No description provided for @inkRed.
  ///
  /// In ar, this message translates to:
  /// **'أحمر'**
  String get inkRed;

  /// No description provided for @inkRedo.
  ///
  /// In ar, this message translates to:
  /// **'إعادة'**
  String get inkRedo;

  /// No description provided for @inkTemporary.
  ///
  /// In ar, this message translates to:
  /// **'الحبر مؤقت — لا يُحفظ ولا يُرفع. مغادرة الصفحة أو تغيير المريض يمسح الحبر.'**
  String get inkTemporary;

  /// No description provided for @inkUndo.
  ///
  /// In ar, this message translates to:
  /// **'تراجع'**
  String get inkUndo;

  /// No description provided for @inkNavigation.
  ///
  /// In ar, this message translates to:
  /// **'إصبع واحد: تحريك · إصبعان: تكبير وتصغير'**
  String get inkNavigation;

  /// No description provided for @inkResetView.
  ///
  /// In ar, this message translates to:
  /// **'إعادة ضبط العرض'**
  String get inkResetView;

  /// No description provided for @inkStylusActive.
  ///
  /// In ar, this message translates to:
  /// **'القلم نشط · التنقل بالأصابع متوقف'**
  String get inkStylusActive;
}

class _AppLocalizationsDelegate
    extends LocalizationsDelegate<AppLocalizations> {
  const _AppLocalizationsDelegate();

  @override
  Future<AppLocalizations> load(Locale locale) {
    return SynchronousFuture<AppLocalizations>(lookupAppLocalizations(locale));
  }

  @override
  bool isSupported(Locale locale) =>
      <String>['ar', 'en'].contains(locale.languageCode);

  @override
  bool shouldReload(_AppLocalizationsDelegate old) => false;
}

AppLocalizations lookupAppLocalizations(Locale locale) {
  // Lookup logic when only language code is specified.
  switch (locale.languageCode) {
    case 'ar':
      return AppLocalizationsAr();
    case 'en':
      return AppLocalizationsEn();
  }

  throw FlutterError(
    'AppLocalizations.delegate failed to load unsupported locale "$locale". This is likely '
    'an issue with the localizations generation tool. Please file an issue '
    'on GitHub with a reproducible sample app and the gen-l10n configuration '
    'that was used.',
  );
}
