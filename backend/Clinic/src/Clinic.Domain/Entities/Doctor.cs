namespace Clinic.Domain.Entities;

public class Doctor
{
    public Guid Id { get; private set; }

    public string FullName { get; private set; } = string.Empty;

    public bool IsActive { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    private Doctor()
    {
    }

    public Doctor(string fullName)
    {
        if (string.IsNullOrWhiteSpace(fullName))
        {
            throw new ArgumentException(
                "Doctor name is required.",
                nameof(fullName));
        }

        Id = Guid.NewGuid();
        FullName = fullName.Trim();
        IsActive = true;
        CreatedAtUtc = DateTimeOffset.UtcNow;
    }

    public void Activate()
    {
        IsActive = true;
    }

    public void Deactivate()
    {
        IsActive = false;
    }
}
