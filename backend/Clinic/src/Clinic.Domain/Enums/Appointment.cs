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
    public Guid DoctorId { get; private set; }
    public string? CancellationReason { get; private set; }

    public string? CancelledByUserId { get; private set; }

    public DateTimeOffset? CancelledAtUtc { get; private set; }

    private Appointment()
    {
    }

    public Appointment(
    Guid patientId,
    Guid doctorId,
    DateTimeOffset startsAt,
    DateTimeOffset endsAt)
    {
        if (patientId == Guid.Empty)
        {
            throw new ArgumentException(
                "Patient ID is required.",
                nameof(patientId));
        }

        if (doctorId == Guid.Empty)
        {
            throw new ArgumentException(
                "Doctor ID is required.",
                nameof(doctorId));
        }

        if (endsAt <= startsAt)
        {
            throw new ArgumentException(
                "Appointment end must be after its start.",
                nameof(endsAt));
        }

        Id = Guid.NewGuid();
        PatientId = patientId;
        DoctorId = doctorId;
        StartsAtUtc = startsAt.ToUniversalTime();
        EndsAtUtc = endsAt.ToUniversalTime();
        Status = AppointmentStatus.Pending;
        CreatedAtUtc = DateTimeOffset.UtcNow;
    }
    public void Confirm()
    {
        if (Status != AppointmentStatus.Pending)
        {
            throw new InvalidOperationException(
                "Only pending  appointments can be confirmed.");
        }

        Status = AppointmentStatus.Confirmed;
    }

    public void Cancel(
    string reason,
    string cancelledByUserId,
    DateTimeOffset cancelledAt)
    {
        if (Status != AppointmentStatus.Pending &&
            Status != AppointmentStatus.Confirmed)
        {
            throw new InvalidOperationException(
                "Only pending or confirmed appointments can be cancelled.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        ArgumentException.ThrowIfNullOrWhiteSpace(cancelledByUserId);

        var trimmedReason = reason.Trim();

        if (trimmedReason.Length > 500)
        {
            throw new ArgumentException(
                "Cancellation reason cannot exceed 500 characters.",
                nameof(reason));
        }

        if (cancelledByUserId.Length > 200)
        {
            throw new ArgumentException(
                "User ID cannot exceed 200 characters.",
                nameof(cancelledByUserId));
        }

        if (cancelledAt == default)
        {
            throw new ArgumentException(
                "Cancellation time is required.",
                nameof(cancelledAt));
        }

        CancellationReason = trimmedReason;
        CancelledByUserId = cancelledByUserId;
        CancelledAtUtc = cancelledAt.ToUniversalTime();
        Status = AppointmentStatus.Cancelled;
    }
    public void Complete(DateTimeOffset now)
    {
        if (Status != AppointmentStatus.InProgress)
        {
            throw new InvalidOperationException(
                "Only appointments in progress can be completed.");
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
        if (Status != AppointmentStatus.Pending &&
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
    public void MarkAsArrived()
    {
        if (Status != AppointmentStatus.Confirmed)
        {
            throw new InvalidOperationException(
                "Only confirmed appointments can be marked as arrived.");
        }

        Status = AppointmentStatus.Arrived;
    }

    public void StartVisit(DateTimeOffset now)
    {
        if (Status != AppointmentStatus.Arrived)
        {
            throw new InvalidOperationException(
                "Only arrived appointments can be started.");
        }

        if (now < StartsAtUtc)
        {
            throw new InvalidOperationException(
                "A visit cannot start before the appointment start.");
        }

        Status = AppointmentStatus.InProgress;
    }
}
