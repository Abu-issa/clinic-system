using System.Globalization;

namespace Clinic.Api.StaffMvc;

// Small shared localization catalog for the first MVC slice; domain text is never translated.
public sealed class StaffText
{
    public bool IsArabic => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";
    public string Culture => IsArabic ? "ar" : "en";
    public string this[string key] => Strings.TryGetValue(key, out var value) ? IsArabic ? value.Ar : value.En : key;
    public string Date(DateTimeOffset? date) => date is null ? this["NotYet"] : date.Value.ToUniversalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC";
    public string Size(long bytes) => bytes < 1024 ? bytes.ToString(CultureInfo.InvariantCulture) + " B"
        : bytes < 1048576 ? (bytes / 1024d).ToString("0.##", CultureInfo.InvariantCulture) + " KB"
        : (bytes / 1048576d).ToString("0.##", CultureInfo.InvariantCulture) + " MB";
    private static readonly Dictionary<string, (string Ar, string En)> Strings = new()
    {
        ["Clinic"] = ("مساحة الفريق الطبي", "Clinical workspace"),
        ["Language"] = ("اللغة", "Language"),
        ["SubmissionExpired"] = ("تم استخدام نموذج الطلب أو انتهت صلاحيته. تحقق من قائمة الفحوصات قبل فتح نموذج جديد لتجنب طلب مكرر.", "This form was already submitted or expired. Check the test list before opening a new form to avoid a duplicate request."),
        ["NoVisits"] = ("لا توجد زيارات متاحة للاختيار ضمن صلاحيتك. يمكنك إنشاء طلب بدون ربط بزيارة.", "No visits are available to select within your access. You can create a standalone request."),
        ["Tests"] = ("الفحوصات الطبية", "Clinical tests"),
        ["Subtitle"] = ("طلبات الفحوصات والنتائج ومراجعتها", "Test requests, result files and review"),
        ["Patient"] = ("المريض", "Patient"), ["Record"] = ("رقم الملف", "Medical record"),
        ["PatientReference"] = ("مرجع المريض", "Patient reference"),
        ["Create"] = ("طلب فحص جديد", "New test request"), ["CreateSubmit"] = ("إنشاء الطلب", "Create request"),
        ["TestName"] = ("اسم الفحص", "Test name"), ["Category"] = ("الفئة", "Category"),
        ["Lab"] = ("مختبر", "Lab"), ["Imaging"] = ("تصوير طبي", "Imaging"),
        ["Status"] = ("الحالة", "Status"), ["Requested"] = ("مطلوب", "Requested"),
        ["Uploaded"] = ("النتائج مرفوعة", "Uploaded"), ["UnderReview"] = ("قيد المراجعة", "Under review"),
        ["Reviewed"] = ("تمت المراجعة", "Reviewed"), ["RequestedAt"] = ("تاريخ الطلب", "Requested"),
        ["UploadedAt"] = ("أول رفع للنتائج", "First result uploaded"), ["ReviewedAt"] = ("تاريخ المراجعة", "Reviewed"),
        ["Results"] = ("ملفات النتائج", "Result files"), ["Count"] = ("عدد الملفات", "Files"),
        ["Open"] = ("عرض التفاصيل", "View details"), ["Download"] = ("تنزيل", "Download"),
        ["DownloadHint"] = ("تُنزل الملفات بشكل آمن؛ افتح الملف بعد التنزيل.", "Files download securely; open them after download."),
        ["NoResults"] = ("لم تُرفع نتائج بعد.", "No result files yet."),
        ["NoTests"] = ("لا توجد طلبات فحوصات ضمن هذه الصفحة.", "No test requests on this page."),
        ["Instructions"] = ("تعليمات سريرية", "Clinical instructions"),
        ["Optional"] = ("اختياري", "Optional"), ["Visit"] = ("الزيارة", "Visit"),
        ["Standalone"] = ("بدون ربط بزيارة", "Standalone request"),
        ["VisitHint"] = ("تظهر أحدث ١٠٠ زيارة متاحة لهذا المريض ضمن صلاحيتك الطبية.", "Shows up to 100 recent patient visits within your clinical authority."),
        ["Back"] = ("العودة إلى الفحوصات", "Back to tests"), ["Cancel"] = ("إلغاء", "Cancel"),
        ["All"] = ("الكل", "All"), ["Filter"] = ("تطبيق الفلاتر", "Apply filters"),
        ["Previous"] = ("السابق", "Previous"), ["Next"] = ("التالي", "Next"), ["Page"] = ("الصفحة", "Page"),
        ["Upload"] = ("رفع نتيجة", "Upload result"), ["File"] = ("ملف النتيجة", "Result file"),
        ["UploadHint"] = ("ملف واحد بصيغة PDF أو JPEG أو PNG. الحد الأقصى:", "One PDF, JPEG or PNG file. Maximum size:"),
        ["UploadJs"] = ("الرفع غير متاح حتى يتم تحميل JavaScript. فعّله ثم حدّث الصفحة للتحقق الآمن قبل إرسال الملف.", "Upload is unavailable until JavaScript loads. Enable it and refresh the page to securely verify the upload."),
        ["StartReview"] = ("بدء المراجعة", "Start review"), ["CompleteReview"] = ("إكمال المراجعة", "Complete review"),
        ["CompleteHint"] = ("راجع جميع ملفات النتائج قبل الإكمال. بعد الإكمال لا يمكن تعديل الطلب أو رفع نتائج إضافية.", "Review all result files before completing. Completion prevents further changes and uploads."),
        ["Locked"] = ("اكتملت مراجعة هذا الطلب. لا يمكن رفع نتائج أو تغيير حالته.", "This request is reviewed. Further uploads and state changes are unavailable."),
        ["HiddenAttachments"] = ("ليست لديك صلاحية عرض ملفات النتائج. العدد فقط متاح ضمن معلومات الفحص.", "You do not have permission to view result files. Only their count is available as test information."),
        ["Name"] = ("اسم الملف", "Filename"), ["Type"] = ("النوع", "Type"), ["Size"] = ("الحجم", "Size"),
        ["CreatedAt"] = ("تاريخ الرفع", "Uploaded"), ["NotYet"] = ("—", "—"),
        ["Saved"] = ("تم حفظ التغيير بنجاح.", "Your change was saved."),
        ["Invalid"] = ("تعذر حفظ الطلب. تحقق من الحقول المطلوبة وطول النصوص ثم أعد المحاولة.", "Unable to save. Check required fields and text lengths, then try again."),
        ["Conflict"] = ("تغير طلب الفحص. حدّث الصفحة ثم حاول مجدداً.", "The test request changed. Refresh and try again."),
        ["Transition"] = ("لا يمكن تنفيذ هذا الإجراء في الحالة الحالية. حدّث الصفحة.", "This action is unavailable in the current state. Refresh the page."),
        ["TooLarge"] = ("حجم الملف أكبر من الحد المسموح.", "The file exceeds the upload limit."),
        ["Unsupported"] = ("الملف غير مدعوم. استخدم PDF أو JPEG أو PNG صالحاً.", "Unsupported file. Choose a valid PDF, JPEG or PNG."),
        ["Csrf"] = ("انتهت صلاحية التحقق. حدّث الصفحة ثم حاول مجدداً.", "Request verification failed. Refresh the page and try again."),
        ["Error"] = ("تعذر إكمال الطلب", "Unable to complete the request"),
        ["401"] = ("سجّل الدخول باستخدام جلسة الموظف مع التحقق متعدد العوامل ثم أعد فتح الصفحة.", "Sign in with an MFA-verified staff session, then reopen this page."),
        ["403"] = ("ليس لديك صلاحية الوصول إلى هذا الإجراء أو المريض.", "You do not have access to this action or patient."),
        ["404"] = ("المورد المطلوب غير متاح.", "The requested resource is unavailable."),
        ["500"] = ("تعذر إكمال العملية. حاول لاحقاً. لا تُعد إرسال الملف قبل التحقق من حالة الطلب.", "Unable to complete the operation. Try later. Check the request before resubmitting a file."),
        ["Skip"] = ("انتقل إلى المحتوى", "Skip to content"), ["Refresh"] = ("تحديث التفاصيل", "Refresh details"),
        ["Sending"] = ("جارٍ الإرسال…", "Submitting…"),
    };
}
