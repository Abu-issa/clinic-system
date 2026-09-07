using Clinic.Domain.Enums;

namespace Clinic.Domain.Entities;

public class Appointment
{
    public Guid Id { get; private set; }

    public Guid PatientId { get; private set; }

    public DateTimeOffset StartsAtUtc { get; private set; }

    public DateTimeOffset EndsAtUtc { get; private set; }

    public AppointmentStatus Status { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    private Appointment()
    {
    }

    public Appointment(
        Guid patientId,
        DateTimeOffset startsAt,
        DateTimeOffset endsAt)
    {
        if (patientId == Guid.Empty)
        {
            throw new ArgumentException(
                "Patient ID is required.",
                nameof(patientId));
        }

        if (endsAt <= startsAt)
        {
            throw new ArgumentException(
                "Appointment end must be after its start.",
                nameof(endsAt));
        }

        Id = Guid.NewGuid();
        PatientId = patientId;
        StartsAtUtc = startsAt.ToUniversalTime();
        EndsAtUtc = endsAt.ToUniversalTime();
        Status = AppointmentStatus.Scheduled;
        CreatedAtUtc = DateTimeOffset.UtcNow;
    }
    public void Confirm()
    {
        if (Status != AppointmentStatus.Scheduled)
        {
            throw new InvalidOperationException(
                "Only scheduled appointments can be confirmed.");
        }

        Status = AppointmentStatus.Confirmed;
    }

    public void Cancel()
    {
        if (Status != AppointmentStatus.Scheduled &&
            Status != AppointmentStatus.Confirmed)
        {
            throw new InvalidOperationException(
                "Only scheduled or confirmed appointments can be cancelled.");
        }

        Status = AppointmentStatus.Cancelled;
    }
    public void Complete(DateTimeOffset now)
    {
        if (Status != AppointmentStatus.Scheduled &&
            Status != AppointmentStatus.Confirmed)
        {
            throw new InvalidOperationException(
                "Only scheduled or confirmed appointments can be completed.");
        }

        if (now < StartsAtUtc)
        {
            throw new InvalidOperationException(
                "An appointment cannot be completed before its start.");
        }

        Status = AppointmentStatus.Completed;
    }

    public void MarkAsNoShow(DateTimeOffset now)
    {
        if (Status != AppointmentStatus.Scheduled &&
            Status != AppointmentStatus.Confirmed)
        {
            throw new InvalidOperationException(
                "Only scheduled or confirmed appointments can be marked as no-show.");
        }

        if (now < EndsAtUtc)
        {
            throw new InvalidOperationException(
                "An appointment cannot be marked as no-show before its end.");
        }

        Status = AppointmentStatus.NoShow;
    }
}
