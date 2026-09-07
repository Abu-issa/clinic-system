using Clinic.Domain.Entities;
using Xunit;

namespace Clinic.UnitTests.Entities;

public class PatientTests
{
    [Fact]
    public void Constructor_WithValidData_CreatesPatient()
    {
        // Arrange: تجهيز البيانات.
        var dateOfBirth = new DateOnly(2003, 5, 12);

        // Act: تنفيذ العملية.
        var patient = new Patient(
            "  محمد أحمد  ",
            "  0791234567  ",
            dateOfBirth);

        // Assert: التحقق من النتيجة.
        Assert.NotEqual(Guid.Empty, patient.Id);
        Assert.Equal("محمد أحمد", patient.FullName);
        Assert.Equal("0791234567", patient.PhoneNumber);
        Assert.Equal<DateOnly?>(dateOfBirth, patient.DateOfBirth);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_WithBlankName_Throws(string fullName)
    {
        var exception = Assert.Throws<ArgumentException>(() =>
        {
            _ = new Patient(fullName, "0791234567");
        });

        Assert.Equal("fullName", exception.ParamName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_WithBlankPhoneNumber_Throws(string phoneNumber)
    {
        var exception = Assert.Throws<ArgumentException>(() =>
        {
            _ = new Patient("محمد أحمد", phoneNumber);
        });

        Assert.Equal("phoneNumber", exception.ParamName);
    }

    [Fact]
    public void UpdateContactDetails_WithValidData_UpdatesPatient()
    {
        var patient = new Patient("محمد أحمد", "0791234567");
        var originalId = patient.Id;

        patient.UpdateContactDetails(
            "  محمد أبو عيسى  ",
            "  0781234567  ");

        Assert.Equal("محمد أبو عيسى", patient.FullName);
        Assert.Equal("0781234567", patient.PhoneNumber);
        Assert.Equal(originalId, patient.Id);
    }

    [Fact]
    public void UpdateContactDetails_WithInvalidPhone_KeepsOriginalData()
    {
        var patient = new Patient("محمد أحمد", "0791234567");

        Assert.Throws<ArgumentException>(() =>
        {
            patient.UpdateContactDetails("اسم جديد", "   ");
        });

        Assert.Equal("محمد أحمد", patient.FullName);
        Assert.Equal("0791234567", patient.PhoneNumber);
    }
}
