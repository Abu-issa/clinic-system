using Clinic.Application.Prescriptions;
using Clinic.Domain.Enums;
using QuestPDF.Drawing;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Clinic.Infrastructure.Printing;

/// <summary>
/// Renders the printable prescription with QuestPDF (Community license) using only reproducibly
/// bundled Noto fonts (SIL OFL 1.1, embedding permitted; license text shipped alongside).
/// Arabic rendering uses RTL content direction and the Arabic font; registered fonts act as the
/// fallback pool for Latin text inside Arabic documents. Document metadata is derived from the
/// prescription data, not from wall-clock time, so identical data renders identical bytes.
/// No HTML/markup interpretation exists: all content is drawn as literal text.
/// </summary>
public sealed class QuestPdfPrescriptionRenderer : IPrescriptionPdfRenderer
{
    private const string ArabicFont = "Noto Sans Arabic";
    private const string LatinFont = "Noto Sans";
    private static readonly object InitializationLock = new();
    private static bool _initialized;

    public QuestPdfPrescriptionRenderer()
    {
        lock (InitializationLock)
        {
            if (_initialized)
                return;
            QuestPDF.Settings.License = LicenseType.Community;
            QuestPDF.Settings.UseSystemFonts = false;
            QuestPDF.Settings.ThrowOnMissingTextGlyphs = true;
            RegisterFont("Clinic.Infrastructure.Fonts.NotoSansArabic-Regular.ttf");
            RegisterFont("Clinic.Infrastructure.Fonts.NotoSans-Regular.ttf");
            _initialized = true;
        }
    }

    // Fonts are referenced by the family name stored in the font file ("Noto Sans Arabic" /
    // "Noto Sans"), per the QuestPDF 2026.9 registration API.
    private static void RegisterFont(string resourceName)
    {
        using var stream = typeof(QuestPdfPrescriptionRenderer).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Bundled font resource '{resourceName}' is missing.");
        FontManager.RegisterFontFromStream(stream);
        if (!FontManager.GetRegisteredFonts().Any(font =>
                font.FamilyName.Contains("Noto Sans", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"Bundled font resource '{resourceName}' did not register a Noto Sans family.");
    }

    public byte[] Render(PrescriptionPrintView view)
    {
        var arabic = view.Language == PrintLanguage.Arabic;
        string T(string ar, string en) => arabic ? ar : en;
        // In RTL paragraphs QuestPDF reverses value segments that begin with a digit
        // ("500 mg" would display as "mg 500"). A leading U+200E (LRM) gives every stored
        // value a strong LTR context so numbers, units and dose direction read correctly;
        // Arabic-script values are unaffected by the invisible marker.
        string V(string value) => arabic ? "\u200E" + value : value;

        return Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(36);
                if (arabic)
                    page.ContentFromRightToLeft();
                // The requested language picks the primary font; the other bundled font is the
                // ordered fallback so stored names in the alternate script always render.
                page.DefaultTextStyle(x => (arabic
                        ? x.FontFamily(ArabicFont, LatinFont)
                        : x.FontFamily(LatinFont, ArabicFont))
                    .FontSize(11));

                page.Header().PaddingBottom(12).Column(column =>
                {
                    column.Item().Text(view.ClinicName).FontSize(18);
                    column.Item().Text(view.ClinicAddressLine).FontSize(9).FontColor(Colors.Grey.Darken2);
                    column.Item().Text(T("هاتف: ", "Phone: ") + view.ClinicPhone).FontSize(9).FontColor(Colors.Grey.Darken2);
                });

                page.Content().PaddingVertical(8).Column(column =>
                {
                    column.Item().PaddingBottom(10).Text(T("وصفة طبية", "Prescription")).FontSize(15);

                    column.Item().Table(table =>
                    {
                        table.ColumnsDefinition(columns =>
                        {
                            columns.ConstantColumn(90);
                            columns.RelativeColumn();
                        });

                        InfoRow(table, T("رقم الوصفة", "Prescription No."), V(view.PrescriptionId.ToString("N")));
                        if (view.FinalizedAtUtc is { } finalized)
                            InfoRow(table, T("تاريخ الوصفة", "Prescription date"),
                                V(finalized.ToString("yyyy-MM-dd HH:mm 'UTC'")));
                        InfoRow(table, T("اسم المريض", "Patient name"), view.PatientName);
                        if (!string.IsNullOrWhiteSpace(view.PatientMedicalRecordNumber))
                            InfoRow(table, T("الرقم الطبي", "Medical record no."), V(view.PatientMedicalRecordNumber!));
                        if (view.PatientDateOfBirth is { } dateOfBirth)
                            InfoRow(table, T("تاريخ الميلاد", "Date of birth"), V(dateOfBirth.ToString("yyyy-MM-dd")));
                        InfoRow(table, T("الطبيب المعالج", "Prescribing doctor"), view.DoctorName);
                    });

                    column.Item().PaddingTop(14).PaddingBottom(4)
                        .Text(T("الأدوية", "Medications")).FontSize(13);

                    column.Item().Table(table =>
                    {
                        table.ColumnsDefinition(columns =>
                        {
                            columns.ConstantColumn(22);
                            columns.RelativeColumn(3);
                            columns.RelativeColumn(2);
                            columns.RelativeColumn(2);
                            columns.RelativeColumn(2);
                            columns.RelativeColumn(2);
                            columns.RelativeColumn(3);
                        });

                        table.Header(header =>
                        {
                            static void Cell(IContainer cell, string text)
                            {
                                cell.Background(Colors.Grey.Lighten3).Padding(4).Text(text).FontSize(9);
                            }
                            Cell(header.Cell(), T("#", "#"));
                            Cell(header.Cell(), T("الدواء", "Medication"));
                            Cell(header.Cell(), T("التركيز", "Strength"));
                            Cell(header.Cell(), T("الجرعة", "Dose"));
                            Cell(header.Cell(), T("التكرار", "Frequency"));
                            Cell(header.Cell(), T("المدة", "Duration"));
                            Cell(header.Cell(), T("تعليمات", "Instructions"));
                        });

                        foreach (var item in view.Items)
                        {
                            void Body(IContainer cell, string text, float size = 10)
                            {
                                cell.Padding(4).Text(V(text)).FontSize(size);
                            }

                            var medication = item.BrandName is { } brand
                                ? $"{item.GenericName} ({brand})"
                                : item.GenericName;
                            var strength = $"{item.Strength} {item.Unit}\n{FormLabel(item.Form, arabic)}\n{RouteLabel(item.Route, arabic)}";

                            Body(table.Cell(), item.DisplayOrder.ToString());
                            Body(table.Cell(), medication + (item.UsedTranslationFallback
                                ? T(" *\n(الاسم الأصلي كما هو محفوظ)", " *\n(original stored name shown)")
                                : string.Empty), 10);
                            Body(table.Cell(), strength, 9);
                            Body(table.Cell(), item.Dose ?? "—");
                            Body(table.Cell(), item.Frequency ?? "—");
                            Body(table.Cell(), item.Duration ?? "—");
                            Body(table.Cell(), item.Instructions ?? "—", 9);
                        }
                    });

                    if (view.Items.Any(x => x.UsedTranslationFallback))
                        column.Item().PaddingTop(6).Text(T(
                            "* لا توجد ترجمة عربية محفوظة لهذا الدواء؛ تم استخدام الاسم المخزّن كما هو.",
                            "* No stored translation exists for this medication; the original stored name is shown."))
                            .FontSize(8).FontColor(Colors.Grey.Darken1);
                });

                page.Footer().Row(row =>
                {
                    row.RelativeItem().Text(view.ClinicPhone).FontSize(8).FontColor(Colors.Grey.Darken1);
                    row.RelativeItem().AlignCenter().Text(text =>
                    {
                        text.DefaultTextStyle(x => x.FontSize(8).FontColor(Colors.Grey.Darken1));
                        text.Span(T("صفحة ", "Page "));
                        text.CurrentPageNumber();
                        text.Span(" / ");
                        text.TotalPages();
                    });
                    row.RelativeItem().AlignRight().Text(view.PrescriptionId.ToString("N")[..8])
                        .FontSize(8).FontColor(Colors.Grey.Darken1);
                });
            });
        })
        .WithMetadata(new DocumentMetadata
        {
            CreationDate = view.FinalizedAtUtc ?? DateTimeOffset.UnixEpoch,
            ModifiedDate = view.FinalizedAtUtc ?? DateTimeOffset.UnixEpoch,
            Creator = "Clinic System",
            Producer = "Clinic System",
            Title = T("وصفة طبية", "Prescription"),
            Language = arabic ? "ar" : "en"
        })
        .GeneratePdf();
    }

    private static void InfoRow(TableDescriptor table, string label, string value)
    {
        table.Cell().Padding(2).Text(label).FontSize(10);
        table.Cell().Padding(2).Text(value).FontSize(10);
    }

    private static string FormLabel(DosageForm form, bool arabic) => form switch
    {
        DosageForm.Tablet => arabic ? "قرص" : "Tablet",
        DosageForm.Capsule => arabic ? "كبسولة" : "Capsule",
        DosageForm.Syrup => arabic ? "شراب" : "Syrup",
        DosageForm.Suspension => arabic ? "معلق" : "Suspension",
        DosageForm.Injection => arabic ? "حقن" : "Injection",
        DosageForm.Ointment => arabic ? "مرهم" : "Ointment",
        DosageForm.Cream => arabic ? "كريم" : "Cream",
        DosageForm.Drops => arabic ? "قطرة" : "Drops",
        DosageForm.Inhaler => arabic ? "بخاخ" : "Inhaler",
        DosageForm.Suppository => arabic ? "لبوس" : "Suppository",
        DosageForm.Patch => arabic ? "لصقة" : "Patch",
        _ => arabic ? "أخرى" : "Other"
    };

    private static string RouteLabel(MedicationRoute route, bool arabic) => route switch
    {
        MedicationRoute.Oral => arabic ? "فموي" : "Oral",
        MedicationRoute.Sublingual => arabic ? "تحت اللسان" : "Sublingual",
        MedicationRoute.Topical => arabic ? "موضعي" : "Topical",
        MedicationRoute.Intravenous => arabic ? "وريدي" : "Intravenous",
        MedicationRoute.Intramuscular => arabic ? "عضلي" : "Intramuscular",
        MedicationRoute.Subcutaneous => arabic ? "تحت الجلد" : "Subcutaneous",
        MedicationRoute.Inhalation => arabic ? "استنشاق" : "Inhalation",
        MedicationRoute.Ophthalmic => arabic ? "عيني" : "Ophthalmic",
        MedicationRoute.Otic => arabic ? "أذني" : "Otic",
        MedicationRoute.Nasal => arabic ? "أنفي" : "Nasal",
        MedicationRoute.Rectal => arabic ? "شرجي" : "Rectal",
        _ => arabic ? "أخرى" : "Other"
    };
}
