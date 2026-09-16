using Clinic.Domain.Entities;
using Clinic.Domain.Enums;

namespace Clinic.Application.Prescriptions;

/// <summary>Explicit printable language. The API accepts exactly "ar" and "en".</summary>
public enum PrintLanguage
{
    Arabic = 1,
    English = 2
}

/// <summary>
/// Configured clinic display details used on every printed document. Contains no secrets.
/// Synthetic placeholder values are acceptable only in development/test; production startup
/// rejects them (see PrintClinicDetails.IsSyntheticPlaceholder).
/// </summary>
public sealed class PrintClinicDetails
{
    public const int MaxNameLength = 200;
    public const int MaxAddressLineLength = 300;
    public const int MaxPhoneLength = 50;

    public string Name { get; init; } = string.Empty;
    public string AddressLine { get; init; } = string.Empty;
    public string Phone { get; init; } = string.Empty;

    /// <summary>Bounded validation shared by configuration startup validation.</summary>
    public bool IsValid() =>
        !string.IsNullOrWhiteSpace(Name) && Name.Length <= MaxNameLength &&
        !string.IsNullOrWhiteSpace(AddressLine) && AddressLine.Length <= MaxAddressLineLength &&
        !string.IsNullOrWhiteSpace(Phone) && Phone.Length <= MaxPhoneLength;

    /// <summary>
    /// Recognizes the shipped development placeholders so production startup can refuse them.
    /// Development/test deliberately use these clearly-synthetic values.
    /// </summary>
    public static bool IsSyntheticPlaceholder(PrintClinicDetails clinic) =>
        clinic.Name.Contains("configure ", StringComparison.OrdinalIgnoreCase) ||
        clinic.Name.StartsWith("Synthetic Clinic", StringComparison.OrdinalIgnoreCase) ||
        clinic.AddressLine.Contains("configure ", StringComparison.OrdinalIgnoreCase) ||
        clinic.Phone.Contains("configure ", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Stored prescription data required for printing, assembled by Infrastructure.</summary>
public sealed record PrescriptionPrintProjection(
    Guid PrescriptionId,
    PrescriptionStatus Status,
    DateTimeOffset? FinalizedAtUtc,
    string PatientName,
    string? PatientMedicalRecordNumber,
    DateOnly? PatientDateOfBirth,
    string DoctorName,
    IReadOnlyList<PrescriptionPrintItemProjection> Items);

/// <summary>Snapshot fields of one item; names carried in both stored languages.</summary>
public sealed record PrescriptionPrintItemProjection(
    int DisplayOrder,
    string? GenericNameEn,
    string? GenericNameAr,
    string? BrandNameEn,
    string? BrandNameAr,
    string Strength,
    string Unit,
    DosageForm Form,
    MedicationRoute Route,
    string? Dose,
    string? Frequency,
    string? Duration,
    string? Instructions);

/// <summary>
/// One rendered medication line. When the requested language lacks a stored translation, the
/// alternate stored snapshot name is used verbatim and UsedTranslationFallback is set so the
/// document can disclose the fallback visibly. No translation is ever invented.
/// </summary>
public sealed record PrescriptionPrintItem(
    int DisplayOrder,
    string GenericName,
    string? BrandName,
    string Strength,
    string Unit,
    DosageForm Form,
    MedicationRoute Route,
    string? Dose,
    string? Frequency,
    string? Duration,
    string? Instructions,
    bool UsedTranslationFallback);

/// <summary>
/// Complete presentation input for the renderer. Deliberately excludes internal notes,
/// clinical/diagnosis fields, rowversions, Visit/Appointment identifiers and security metadata:
/// a printable PDF is a presentation derivative of the stored prescription record only.
/// </summary>
public sealed record PrescriptionPrintView(
    Guid PrescriptionId,
    PrintLanguage Language,
    string ClinicName,
    string ClinicAddressLine,
    string ClinicPhone,
    DateTimeOffset? FinalizedAtUtc,
    string PatientName,
    string? PatientMedicalRecordNumber,
    DateOnly? PatientDateOfBirth,
    string DoctorName,
    IReadOnlyList<PrescriptionPrintItem> Items);

public interface IPrescriptionPrintStore
{
    /// <summary>Null when the prescription does not exist for the route patient.</summary>
    Task<PrescriptionPrintProjection?> GetAsync(Guid patientId, Guid prescriptionId, CancellationToken ct);
}

public interface IPrescriptionPdfRenderer
{
    byte[] Render(PrescriptionPrintView view);
}

public sealed class PrescriptionRenderingException : Exception
{
    public PrescriptionRenderingException(Exception innerException)
        : base("The prescription document could not be rendered.", innerException)
    {
    }
}

public sealed record PrescriptionPdfResult(bool IsSuccess, PrescriptionError Error, byte[]? Pdf)
{
    public static PrescriptionPdfResult Success(byte[] pdf) => new(true, PrescriptionError.None, pdf);
    public static PrescriptionPdfResult Failure(PrescriptionError error) => new(false, error, null);
}

/// <summary>
/// Assembles the printable projection into a render view and produces the document.
/// Printing is presentation-only: it never mutates the prescription or any other aggregate.
/// </summary>
public sealed class PrescriptionPrintService(
    IPrescriptionPrintStore store,
    IPrescriptionPdfRenderer renderer,
    PrintClinicDetails clinic)
{
    public async Task<PrescriptionPdfResult> GetPdfAsync(
        Guid patientId, Guid prescriptionId, PrintLanguage language, CancellationToken ct = default)
    {
        var projection = await store.GetAsync(patientId, prescriptionId, ct);
        if (projection is null)
            return PrescriptionPdfResult.Failure(PrescriptionError.PrescriptionNotFound);
        // Only clinically immutable prescriptions are printable; Draft and Cancelled never are.
        if (projection.Status is not (PrescriptionStatus.Finalized or PrescriptionStatus.Released))
            return PrescriptionPdfResult.Failure(PrescriptionError.NotPrintable);
        // Defensive reuse of the single authoritative domain limit: the domain already refuses
        // over-limit items at creation, so this rejection is unreachable via application paths
        // and exists only for privileged direct writes.
        if (projection.Items.Count > Prescription.MaxItemCount)
            return PrescriptionPdfResult.Failure(PrescriptionError.InvalidInput);

        var view = new PrescriptionPrintView(
            projection.PrescriptionId,
            language,
            clinic.Name,
            clinic.AddressLine,
            clinic.Phone,
            projection.FinalizedAtUtc,
            projection.PatientName,
            projection.PatientMedicalRecordNumber,
            projection.PatientDateOfBirth,
            projection.DoctorName,
            projection.Items.Select(x => MapItem(x, language)).ToArray());

        try
        {
            return PrescriptionPdfResult.Success(renderer.Render(view));
        }
        catch (Exception exception)
        {
            // Sanitized by the API's exception handler; no clinical values are attached.
            throw new PrescriptionRenderingException(exception);
        }
    }

    private static PrescriptionPrintItem MapItem(PrescriptionPrintItemProjection item, PrintLanguage language)
    {
        var arabic = language == PrintLanguage.Arabic;
        var generic = arabic ? item.GenericNameAr ?? item.GenericNameEn : item.GenericNameEn ?? item.GenericNameAr;
        var brand = arabic ? item.BrandNameAr ?? item.BrandNameEn : item.BrandNameEn ?? item.BrandNameAr;
        var fallback = arabic ? item.GenericNameAr is null : item.GenericNameEn is null;
        return new PrescriptionPrintItem(
            item.DisplayOrder, generic!, brand, item.Strength, item.Unit, item.Form, item.Route,
            item.Dose, item.Frequency, item.Duration, item.Instructions, fallback);
    }
}
