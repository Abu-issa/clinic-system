using Clinic.Application.Appointments;

namespace Clinic.UnitTests.Appointments;

public sealed class RescheduleAppointmentResultTests
{
    [Fact]
    public void Success_PreservesVersionSnapshot()
    {
        var version = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        var changeId = Guid.NewGuid();
        var result = RescheduleAppointmentResult.Success(changeId, version);
        version[0] = 99;
        var returned = result.RowVersion!;
        returned[1] = 99;
        Assert.True(result.IsSuccess);
        Assert.Equal(changeId, result.ChangeId);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, result.RowVersion);
    }

    [Fact]
    public void Conflict_DoesNotReturnSuccessfulChangeOrVersion()
    {
        var result = RescheduleAppointmentResult.Failure(ReschedulingError.AppointmentChanged);
        Assert.False(result.IsSuccess);
        Assert.Null(result.ChangeId);
        Assert.Null(result.RowVersion);
    }
}
