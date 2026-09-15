namespace Clinic.Domain.Entities;

public class Patient
{
    public Guid Id { get; private set; }

    // Null only for rows that predate the medical-record foundation; the clinic
    // must explicitly assign legacy MRNs before enforcing them as required.
    public string? MedicalRecordNumber { get; private set; }

    public string? LegacyPaperFileNumber { get; private set; }

    // Opaque reference to a scanned legacy paper cover image. No storage
    // implementation exists in this slice; the value is metadata only.
    public string? LegacyCoverImageReference { get; private set; }

    public string FullName { get; private set; } = string.Empty;

    public string PhoneNumber { get; private set; } = string.Empty;

    public DateOnly? DateOfBirth { get; private set; }

    public string? EmergencyContactName { get; private set; }

    public string? EmergencyContactPhone { get; private set; }

    public string? EmergencyContactRelation { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public byte[] RowVersion { get; private set; } = Array.Empty<byte>();

    private Patient()
    {
    }

    // Legacy-style construction for rows without an assigned MRN (pre-foundation
    // data and migration tests). New records from the API must supply an MRN.
    public Patient(
    string fullName,
    string phoneNumber,
    DateOnly? dateOfBirth = null)
    {
        UpdateContactDetails(fullName, phoneNumber);

        Id = Guid.NewGuid();
        DateOfBirth = dateOfBirth;
        CreatedAtUtc = DateTimeOffset.UtcNow;
    }

    public Patient(
    string medicalRecordNumber,
    string fullName,
    string phoneNumber,
    DateOnly? dateOfBirth,
    string? legacyPaperFileNumber,
    string? legacyCoverImageReference)
    {
        UpdateContactDetails(fullName, phoneNumber);
        UpdateLegacyReferences(legacyPaperFileNumber, legacyCoverImageReference);

        if (string.IsNullOrWhiteSpace(medicalRecordNumber) ||
            medicalRecordNumber.Trim().Length > 32)
        {
            throw new ArgumentException(
                "A medical record number of up to 32 characters is required.",
                nameof(medicalRecordNumber));
        }

        if (dateOfBirth > DateOnly.FromDateTime(DateTimeOffset.UtcNow.UtcDateTime))
        {
            throw new ArgumentException(
                "Date of birth cannot be in the future.",
                nameof(dateOfBirth));
        }

        Id = Guid.NewGuid();
        MedicalRecordNumber = medicalRecordNumber.Trim();
        DateOfBirth = dateOfBirth;
        CreatedAtUtc = DateTimeOffset.UtcNow;
    }

    public void UpdateContactDetails(string fullName, string phoneNumber)
    {
        if (string.IsNullOrWhiteSpace(fullName))
        {
            throw new ArgumentException(
                "Patient name is required.",
                nameof(fullName));
        }

        if (string.IsNullOrWhiteSpace(phoneNumber))
        {
            throw new ArgumentException(
                "Phone number is required.",
                nameof(phoneNumber));
        }

        FullName = fullName.Trim();
        PhoneNumber = phoneNumber.Trim();
    }

    public void UpdateAdministrativeDetails(
    string fullName,
    string phoneNumber,
    DateOnly? dateOfBirth,
    string? emergencyContactName,
    string? emergencyContactPhone,
    string? emergencyContactRelation)
    {
        if (dateOfBirth > DateOnly.FromDateTime(DateTimeOffset.UtcNow.UtcDateTime))
        {
            throw new ArgumentException(
                "Date of birth cannot be in the future.",
                nameof(dateOfBirth));
        }

        UpdateContactDetails(fullName, phoneNumber);

        var hasName = !string.IsNullOrWhiteSpace(emergencyContactName);
        var hasPhone = !string.IsNullOrWhiteSpace(emergencyContactPhone);
        var hasRelation = !string.IsNullOrWhiteSpace(emergencyContactRelation);

        if (hasName != hasPhone)
        {
            throw new ArgumentException(
                "An emergency contact requires both a name and a phone number.",
                nameof(emergencyContactName));
        }

        if (!hasName && hasRelation)
        {
            throw new ArgumentException(
                "An emergency contact relation requires a contact name.",
                nameof(emergencyContactRelation));
        }

        DateOfBirth = dateOfBirth;
        EmergencyContactName = hasName ? emergencyContactName!.Trim() : null;
        EmergencyContactPhone = hasPhone ? emergencyContactPhone!.Trim() : null;
        EmergencyContactRelation = hasRelation ? emergencyContactRelation!.Trim() : null;
    }

    public void UpdateLegacyReferences(
    string? legacyPaperFileNumber,
    string? legacyCoverImageReference)
    {
        if (legacyPaperFileNumber?.Trim().Length > 32)
        {
            throw new ArgumentException(
                "Legacy paper file number cannot exceed 32 characters.",
                nameof(legacyPaperFileNumber));
        }

        if (legacyCoverImageReference?.Trim().Length > 512)
        {
            throw new ArgumentException(
                "Legacy cover image reference cannot exceed 512 characters.",
                nameof(legacyCoverImageReference));
        }

        LegacyPaperFileNumber =
            string.IsNullOrWhiteSpace(legacyPaperFileNumber) ? null : legacyPaperFileNumber.Trim();
        LegacyCoverImageReference =
            string.IsNullOrWhiteSpace(legacyCoverImageReference) ? null : legacyCoverImageReference.Trim();
    }
    // Additional methods for updating other properties can be added here
}
