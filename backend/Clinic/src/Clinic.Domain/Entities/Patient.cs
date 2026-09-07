namespace Clinic.Domain.Entities;

public class Patient
{
    public Guid Id { get; private set; }

    public string FullName { get; private set; } = string.Empty;

    public string PhoneNumber { get; private set; } = string.Empty;

    public DateOnly? DateOfBirth { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    private Patient()
    {
    }

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
}
