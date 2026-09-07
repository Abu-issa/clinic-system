using Clinic.Domain.Entities;
using Clinic.Domain.Enums;
using Xunit;

namespace Clinic.UnitTests.Entities;

public class AppointmentTests
{
    [Fact]
    public void Constructor_WithValidData_CreatesScheduledAppointment()
    {
        var patientId = Guid.NewGuid();

        var start = new DateTimeOffset(
            2026, 10, 1, 10, 0, 0, TimeSpan.FromHours(3));

        var end = start.AddMinutes(30);

        var appointment = new Appointment(patientId, start, end);

        Assert.NotEqual(Guid.Empty, appointment.Id);
        Assert.Equal(patientId, appointment.PatientId);
        Assert.Equal(AppointmentStatus.Scheduled, appointment.Status);

        Assert.Equal(start.ToUniversalTime(), appointment.StartsAtUtc);
        Assert.Equal(end.ToUniversalTime(), appointment.EndsAtUtc);

        Assert.Equal(TimeSpan.Zero, appointment.StartsAtUtc.Offset);
        Assert.Equal(TimeSpan.Zero, appointment.EndsAtUtc.Offset);
    }

    [Fact]
    public void Constructor_WithEmptyPatientId_Throws()
    {
        var start = new DateTimeOffset(
            2026, 10, 1, 10, 0, 0, TimeSpan.Zero);

        var exception = Assert.Throws<ArgumentException>(() =>
        {
            _ = new Appointment(
                Guid.Empty,
                start,
                start.AddMinutes(30));
        });

        Assert.Equal("patientId", exception.ParamName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-30)]
    public void Constructor_WithInvalidEndTime_Throws(int durationMinutes)
    {
        var start = new DateTimeOffset(
            2026, 10, 1, 10, 0, 0, TimeSpan.Zero);

        var end = start.AddMinutes(durationMinutes);

        var exception = Assert.Throws<ArgumentException>(() =>
        {
            _ = new Appointment(Guid.NewGuid(), start, end);
        });

        Assert.Equal("endsAt", exception.ParamName);
    }
    [Fact]
    public void Confirm_WhenScheduled_ChangesStatusToConfirmed()
    {
        var appointment = CreateScheduledAppointment();

        appointment.Confirm();

        Assert.Equal(AppointmentStatus.Confirmed, appointment.Status);
    }

    [Fact]
    public void Confirm_WhenCancelled_ThrowsAndKeepsStatus()
    {
        var appointment = CreateScheduledAppointment();
        appointment.Cancel();

        Assert.Throws<InvalidOperationException>(() =>
        {
            appointment.Confirm();
        });

        Assert.Equal(AppointmentStatus.Cancelled, appointment.Status);
    }

    [Fact]
    public void Confirm_WhenAlreadyConfirmed_Throws()
    {
        var appointment = CreateScheduledAppointment();
        appointment.Confirm();

        Assert.Throws<InvalidOperationException>(() =>
        {
            appointment.Confirm();
        });

        Assert.Equal(AppointmentStatus.Confirmed, appointment.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Cancel_WhenScheduledOrConfirmed_ChangesStatusToCancelled(
        bool confirmFirst)
    {
        var appointment = CreateScheduledAppointment();

        if (confirmFirst)
        {
            appointment.Confirm();
        }

        appointment.Cancel();

        Assert.Equal(AppointmentStatus.Cancelled, appointment.Status);
    }

    [Fact]
    public void Cancel_WhenAlreadyCancelled_Throws()
    {
        var appointment = CreateScheduledAppointment();
        appointment.Cancel();

        Assert.Throws<InvalidOperationException>(() =>
        {
            appointment.Cancel();
        });

        Assert.Equal(AppointmentStatus.Cancelled, appointment.Status);
    }

    private static Appointment CreateScheduledAppointment()
    {
        var start = new DateTimeOffset(
            2026, 10, 1, 10, 0, 0, TimeSpan.Zero);

        return new Appointment(
            Guid.NewGuid(),
            start,
            start.AddMinutes(30));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Complete_WhenActiveAndStarted_CompletesAppointment(
    bool confirmFirst)
    {
        var appointment = CreateScheduledAppointment();

        if (confirmFirst)
        {
            appointment.Confirm();
        }

        appointment.Complete(appointment.StartsAtUtc);

        Assert.Equal(AppointmentStatus.Completed, appointment.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MarkAsNoShow_WhenActiveAndEnded_ChangesStatus(
        bool confirmFirst)
    {
        var appointment = CreateScheduledAppointment();

        if (confirmFirst)
        {
            appointment.Confirm();
        }

        appointment.MarkAsNoShow(appointment.EndsAtUtc);

        Assert.Equal(AppointmentStatus.NoShow, appointment.Status);
    }

    [Fact]
    public void Complete_BeforeStart_ThrowsAndKeepsStatus()
    {
        var appointment = CreateScheduledAppointment();

        Assert.Throws<InvalidOperationException>(() =>
        {
            appointment.Complete(
                appointment.StartsAtUtc.AddSeconds(-1));
        });

        Assert.Equal(AppointmentStatus.Scheduled, appointment.Status);
    }

    [Fact]
    public void MarkAsNoShow_BeforeEnd_ThrowsAndKeepsStatus()
    {
        var appointment = CreateScheduledAppointment();

        Assert.Throws<InvalidOperationException>(() =>
        {
            appointment.MarkAsNoShow(
                appointment.EndsAtUtc.AddSeconds(-1));
        });

        Assert.Equal(AppointmentStatus.Scheduled, appointment.Status);
    }

    [Theory]
    [InlineData(AppointmentStatus.Cancelled)]
    [InlineData(AppointmentStatus.Completed)]
    [InlineData(AppointmentStatus.NoShow)]
    public void FinalStatus_RejectsAllStatusChanges(
        AppointmentStatus finalStatus)
    {
        var appointment = CreateScheduledAppointment();
        var now = appointment.EndsAtUtc;

        switch (finalStatus)
        {
            case AppointmentStatus.Cancelled:
                appointment.Cancel();
                break;

            case AppointmentStatus.Completed:
                appointment.Complete(now);
                break;

            case AppointmentStatus.NoShow:
                appointment.MarkAsNoShow(now);
                break;
        }

        Assert.Throws<InvalidOperationException>(() => appointment.Confirm());
        Assert.Throws<InvalidOperationException>(() => appointment.Cancel());
        Assert.Throws<InvalidOperationException>(() => appointment.Complete(now));
        Assert.Throws<InvalidOperationException>(() => appointment.MarkAsNoShow(now));

        Assert.Equal(finalStatus, appointment.Status);
    }
}
