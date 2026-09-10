namespace Clinic.Domain.Entities;

public sealed class DoctorDayClosure
{
    private DoctorDayClosure()
    {
    }

    public DoctorDayClosure(
        Guid doctorId,
        DateOnly localDate,
        string reason)
    {
        if (doctorId == Guid.Empty)
        {
            throw new ArgumentException(
                "Doctor ID is required.",
                nameof(doctorId));
        }

        if (localDate == default)
        {
            throw new ArgumentException(
                "Closure date is required.",
                nameof(localDate));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        var trimmedReason = reason.Trim();

        if (trimmedReason.Length > 500)
        {
            throw new ArgumentException(
                "Closure reason cannot exceed 500 characters.",
                nameof(reason));
        }

        Id = Guid.NewGuid();
        DoctorId = doctorId;
        LocalDate = localDate;
        Reason = trimmedReason;
    }

    public Guid Id { get; private set; }

    public Guid DoctorId { get; private set; }

    public DateOnly LocalDate { get; private set; }

    public string Reason { get; private set; } = string.Empty;
}
