using Clinic.Domain.Entities;
using Clinic.Domain.Enums;

namespace Clinic.UnitTests;

public sealed class ClinicalTestDomainTests
{
    private static ClinicalTestRequest Create(string name = "CBC فحص دم", string? instructions = "تعليمات English",
        ClinicalTestCategory category = ClinicalTestCategory.Lab, Guid? visitId = null) =>
        new(Guid.NewGuid(), visitId, Guid.NewGuid(), category, name, instructions,
            new DateTimeOffset(2026, 9, 17, 12, 0, 0, TimeSpan.FromHours(3)));

    [Theory]
    [InlineData(ClinicalTestCategory.Lab)]
    [InlineData(ClinicalTestCategory.Imaging)]
    public void NewOrderHasOnlyRequestedStateAndUtcTime(ClinicalTestCategory category)
    {
        var request = Create(category: category);
        Assert.Equal(category, request.Category);
        Assert.Equal("CBC فحص دم", request.TestName);
        Assert.Equal("تعليمات English", request.ClinicalInstructions);
        Assert.Equal(ClinicalTestStatus.Requested, request.Status);
        Assert.Null(request.VisitId);
        Assert.Null(request.UploadedAtUtc);
        Assert.Null(request.ReviewedAtUtc);
        Assert.Null(request.ReviewedByDoctorId);
        Assert.Empty(request.RowVersion);
        Assert.Equal(new DateTimeOffset(2026, 9, 17, 9, 0, 0, TimeSpan.Zero), request.RequestedAtUtc);
    }

    [Theory]
    [InlineData("patient")]
    [InlineData("doctor")]
    [InlineData("visit")]
    public void EmptyIdentifiersAreRejected(string empty) => Assert.Throws<ArgumentException>(() =>
        new ClinicalTestRequest(empty == "patient" ? Guid.Empty : Guid.NewGuid(), empty == "visit" ? Guid.Empty : null,
            empty == "doctor" ? Guid.Empty : Guid.NewGuid(), ClinicalTestCategory.Lab, "CBC", null, DateTimeOffset.UtcNow));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("CBC\n")]
    [InlineData("CBC\t")]
    [InlineData("CBC\0")]
    public void BlankOrControlNamesAreRejected(string? name) => Assert.Throws<ArgumentException>(() => Create(name!));

    [Theory]
    [InlineData("\r")]
    [InlineData("\n")]
    [InlineData("\t")]
    [InlineData("\0")]
    public void InstructionControlsAreRejected(string instructions) => Assert.Throws<ArgumentException>(() => Create(instructions: instructions));

    [Fact]
    public void TextBoundsAndOptionalVisitArePreserved()
    {
        var visit = Guid.NewGuid();
        var request = Create(new string('ع', 200), new string('a', 2000), visitId: visit);
        Assert.Equal(visit, request.VisitId);
        Assert.Equal(200, request.TestName.Length);
        Assert.Equal(2000, request.ClinicalInstructions!.Length);
        Assert.Throws<ArgumentException>(() => Create(new string('a', 201)));
        Assert.Throws<ArgumentException>(() => Create(instructions: new string('a', 2001)));
        Assert.Null(Create(instructions: " ").ClinicalInstructions);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    [InlineData(999)]
    public void InvalidCategoryIsRejected(int category) => Assert.Throws<ArgumentException>(() => Create(category: (ClinicalTestCategory)category));

    [Fact]
    public void NoPublicSettersTransitionsOrDeleteSurface()
    {
        var type = typeof(ClinicalTestRequest);
        Assert.All(type.GetProperties(), property => Assert.False(property.SetMethod?.IsPublic == true));
        Assert.DoesNotContain(type.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.DeclaredOnly), x => !x.IsSpecialName);
        Assert.DoesNotContain(type.GetProperties(), x => x.Name.Contains("Attachment") || x.Name.Contains("StoredFile"));
    }
}
