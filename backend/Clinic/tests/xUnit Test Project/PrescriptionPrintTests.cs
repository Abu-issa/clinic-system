using Clinic.Application.Prescriptions;
using Clinic.Domain.Entities;
using Clinic.Domain.Enums;
using Clinic.Infrastructure.Printing;

namespace Clinic.UnitTests;

public sealed class PrescriptionPrintTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 16, 10, 0, 0, TimeSpan.Zero);
    private static readonly Guid PrescriptionId = Guid.NewGuid();

    private sealed class FakeStore(PrescriptionPrintProjection? projection) : IPrescriptionPrintStore
    {
        public Task<PrescriptionPrintProjection?> GetAsync(Guid patientId, Guid prescriptionId, CancellationToken ct) =>
            Task.FromResult(projection);
    }

    private sealed class CapturingRenderer : IPrescriptionPdfRenderer
    {
        public PrescriptionPrintView? Rendered { get; private set; }
        public byte[] Render(PrescriptionPrintView view)
        {
            Rendered = view;
            return [0x25, 0x50, 0x44, 0x46]; // %PDF
        }
    }

    private static PrescriptionPrintProjection Projection(PrescriptionStatus status, params PrescriptionPrintItemProjection[] items) => new(
        PrescriptionId, status, status is PrescriptionStatus.Draft ? null : Now,
        "Synthetic Patient", "MRN-77", new DateOnly(1990, 5, 1), "Synthetic Doctor",
        items.Length == 0 ? [new PrescriptionPrintItemProjection(0, "Ibuprofen", null, "Advil", null,
            "500", "mg", DosageForm.Tablet, MedicationRoute.Oral, "1 tablet", "twice daily", "7 days", "after meals")] : items);

    private static PrescriptionPrintService Service(PrescriptionPrintProjection? projection, CapturingRenderer? renderer = null) =>
        new(new FakeStore(projection), renderer ?? new CapturingRenderer(),
            new PrintClinicDetails { Name = "Synthetic Clinic", AddressLine = "Synthetic Address", Phone = "Synthetic Phone" });

    [Theory]
    [InlineData(PrescriptionStatus.Draft)]
    [InlineData(PrescriptionStatus.Cancelled)]
    public async Task DraftAndCancelledPrescriptionsAreNotPrintable(PrescriptionStatus status)
    {
        var result = await Service(Projection(status)).GetPdfAsync(Guid.NewGuid(), PrescriptionId, PrintLanguage.Arabic);
        Assert.False(result.IsSuccess);
        Assert.Equal(PrescriptionError.NotPrintable, result.Error);
        Assert.Null(result.Pdf);
    }

    [Theory]
    [InlineData(PrescriptionStatus.Finalized)]
    [InlineData(PrescriptionStatus.Released)]
    public async Task FinalizedAndReleasedPrescriptionsProduceViews(PrescriptionStatus status)
    {
        var renderer = new CapturingRenderer();
        var result = await Service(Projection(status), renderer).GetPdfAsync(Guid.NewGuid(), PrescriptionId, PrintLanguage.English);
        Assert.True(result.IsSuccess, result.Error.ToString());
        var view = renderer.Rendered!;
        Assert.Equal(PrintLanguage.English, view.Language);
        Assert.Equal("Synthetic Clinic", view.ClinicName);
        Assert.Equal("Synthetic Patient", view.PatientName);
        Assert.Equal("MRN-77", view.PatientMedicalRecordNumber);
        Assert.Equal(new DateOnly(1990, 5, 1), view.PatientDateOfBirth);
        Assert.Equal("Synthetic Doctor", view.DoctorName);
        Assert.Equal(Now, view.FinalizedAtUtc);
        var item = Assert.Single(view.Items);
        Assert.Equal("Ibuprofen", item.GenericName);
        Assert.Equal("Advil", item.BrandName);
        Assert.False(item.UsedTranslationFallback);
    }

    [Fact]
    public async Task MissingRequestedTranslationFallsBackToTheStoredAlternateName()
    {
        var projection = Projection(PrescriptionStatus.Finalized,
            new PrescriptionPrintItemProjection(0, "Ibuprofen", null, "Advil", null, "500", "mg",
                DosageForm.Tablet, MedicationRoute.Oral, "1 tablet", "twice daily", "7 days", null),
            new PrescriptionPrintItemProjection(1, null, "باراسيتامول", null, "بنادول", "500", "mg",
                DosageForm.Tablet, MedicationRoute.Oral, "1 tablet", "twice daily", "7 days", null));
        var renderer = new CapturingRenderer();

        await Service(projection, renderer).GetPdfAsync(Guid.NewGuid(), PrescriptionId, PrintLanguage.Arabic);
        var arabicItems = renderer.Rendered!.Items.ToArray();
        Assert.Equal("Ibuprofen", arabicItems[0].GenericName); // stored alternate, not invented
        Assert.True(arabicItems[0].UsedTranslationFallback);
        Assert.Equal("باراسيتامول", arabicItems[1].GenericName);
        Assert.False(arabicItems[1].UsedTranslationFallback);

        await Service(projection, renderer).GetPdfAsync(Guid.NewGuid(), PrescriptionId, PrintLanguage.English);
        var englishItems = renderer.Rendered!.Items.ToArray();
        Assert.Equal("Ibuprofen", englishItems[0].GenericName);
        Assert.False(englishItems[0].UsedTranslationFallback);
        Assert.Equal("باراسيتامول", englishItems[1].GenericName);
        Assert.True(englishItems[1].UsedTranslationFallback);
    }

    [Fact]
    public void PrintViewCarriesNoInternalClinicalOrSecurityFields()
    {
        // The view type cannot carry what it does not declare: no visit/diagnosis/notes fields,
        // no rowversion, no staff IDs. Verify the allowed property set explicitly.
        var viewProperties = typeof(PrescriptionPrintView).GetProperties().Select(p => p.Name).Order().ToArray();
        Assert.Equal(new[] { "ClinicAddressLine", "ClinicName", "ClinicPhone", "DoctorName",
            "FinalizedAtUtc", "Items", "Language", "PatientDateOfBirth", "PatientMedicalRecordNumber",
            "PatientName", "PrescriptionId" }, viewProperties);
        var itemProperties = typeof(PrescriptionPrintItem).GetProperties().Select(p => p.Name).Order().ToArray();
        Assert.Equal(new[] { "BrandName", "DisplayOrder", "Dose", "Duration", "Form", "Frequency",
            "GenericName", "Instructions", "Route", "Strength", "Unit", "UsedTranslationFallback" }, itemProperties);
    }

    [Fact]
    public async Task ExcessiveItemCountIsRejectedBeforeRendering()
    {
        var items = Enumerable.Range(0, Prescription.MaxItemCount + 1)
            .Select(i => new PrescriptionPrintItemProjection(i, $"Med{i}", null, null, null, "1", "mg",
                DosageForm.Tablet, MedicationRoute.Oral, "1", "daily", "1 day", null))
            .ToArray();
        var result = await Service(Projection(PrescriptionStatus.Finalized, items))
            .GetPdfAsync(Guid.NewGuid(), PrescriptionId, PrintLanguage.English);
        Assert.False(result.IsSuccess);
        Assert.Equal(PrescriptionError.InvalidInput, result.Error);
    }

    [Fact]
    public void RealRendererProducesValidReproduciblePdfsForBothLanguages()
    {
        var renderer = new QuestPdfPrescriptionRenderer(
            new PrintLicenseOptions { PdfLicenseType = "Community" });
        var view = new PrescriptionPrintView(
            PrescriptionId, PrintLanguage.Arabic, "Synthetic Clinic", "Synthetic Address", "Synthetic Phone",
            Now, "Synthetic Patient", "MRN-77", new DateOnly(1990, 5, 1), "Synthetic Doctor",
            new[]
            {
                new PrescriptionPrintItem(0, "إيبوبروفين", "براند", "500", "mg", DosageForm.Tablet,
                    MedicationRoute.Oral, "1 tablet", "twice daily", "7 days", "after meals", false),
                new PrescriptionPrintItem(1, "Paracetamol", null, "500", "mg", DosageForm.Capsule,
                    MedicationRoute.Oral, "2 tablets", "every 6 hours", "5 days", null, true)
            });

        var arabic = renderer.Render(view);
        Assert.Equal(0x25, arabic[0]);
        Assert.Equal(0x50, arabic[1]);
        Assert.Equal(0x44, arabic[2]);
        Assert.Equal(0x46, arabic[3]);
        Assert.Equal(arabic, renderer.Render(view)); // reproducible: identical data, identical bytes

        var english = renderer.Render(view with { Language = PrintLanguage.English });
        Assert.Equal(0x25, english[0]);
        Assert.Equal(0x44, english[2]);
        Assert.NotEqual(arabic, english); // different fonts/layout yield a different document
    }
}
