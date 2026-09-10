namespace Clinic.Domain.Entities;

public class DoctorWorkingPeriod
{
    public Guid Id { get; private set; }

    public Guid DoctorId { get; private set; }

    public DayOfWeek DayOfWeek { get; private set; }

    public TimeOnly StartsAtLocal { get; private set; }

    public TimeOnly EndsAtLocal { get; private set; }

    public bool IsActive { get; private set; }

    private DoctorWorkingPeriod()
    {
    }

    public DoctorWorkingPeriod(
        Guid doctorId,
        DayOfWeek dayOfWeek,
        TimeOnly startsAtLocal,
        TimeOnly endsAtLocal)
    {
        if (doctorId == Guid.Empty)
        {
            throw new ArgumentException(
                "Doctor ID is required.",
                nameof(doctorId));
        }

        if (!Enum.IsDefined(typeof(DayOfWeek), dayOfWeek))
        {
            throw new ArgumentOutOfRangeException(
                nameof(dayOfWeek),
                "The day of week is invalid.");
        }

        if (endsAtLocal <= startsAtLocal)
        {
            throw new ArgumentException(
                "Working period end must be after its start.",
                nameof(endsAtLocal));
        }

        Id = Guid.NewGuid();
        DoctorId = doctorId;
        DayOfWeek = dayOfWeek;
        StartsAtLocal = startsAtLocal;
        EndsAtLocal = endsAtLocal;
        IsActive = true;
    }
}
