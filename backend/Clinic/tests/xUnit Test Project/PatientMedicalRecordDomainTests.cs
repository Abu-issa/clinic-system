using Clinic.Domain.Entities;
using Clinic.Domain.Enums;

namespace Clinic.UnitTests;

public sealed class PatientMedicalRecordDomainTests
{
    [Fact]
    public void LegacyPatientKeepsNullMrn() => Assert.Null(new Patient("Synthetic", "shared").MedicalRecordNumber);

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(null)]
    public void NewPatientRequiresMrn(string? mrn) =>
        Assert.Throws<ArgumentException>(() => new Patient(mrn!, "Synthetic", "shared", null, null, null));

    [Fact]
    public void MrnAndPaperNumberAreSeparate()
    {
        var p = new Patient(" MRN-1 ", "Synthetic", "shared", null, " PAPER-9 ", null);
        Assert.Equal("MRN-1", p.MedicalRecordNumber);
        Assert.Equal("PAPER-9", p.LegacyPaperFileNumber);
    }

    [Fact]
    public void UnknownAndNoKnownAllergiesAreDistinct()
    {
        var p = Profile();
        Assert.Equal(AllergyStatus.Unknown, p.AllergyStatus);
        p.UpdateBasics(null, AllergyStatus.NoKnownAllergies, null, null, "staff", DateTimeOffset.UtcNow);
        Assert.Equal(AllergyStatus.NoKnownAllergies, p.AllergyStatus);
        p.RecordAllergy("synthetic", null, null, MedicalRecordSource.Patient, "staff", DateTimeOffset.UtcNow);
        Assert.Equal(AllergyStatus.HasKnownAllergies, p.AllergyStatus);
        Assert.Throws<InvalidOperationException>(() => p.UpdateBasics(null, AllergyStatus.NoKnownAllergies, null, null, "staff", DateTimeOffset.UtcNow));
    }

    [Fact]
    public void PatientSourceRequiresReviewAndRetainsProvenance()
    {
        var p = Profile();
        var now = DateTimeOffset.UtcNow;
        var a = p.RecordAllergy("synthetic", null, null, MedicalRecordSource.Patient, "recorder", now);
        Assert.Equal(ClinicalReviewStatus.PendingReview, a.ReviewStatus);
        Assert.Null(a.VerifiedByStaffId);
        p.VerifyEntry(a, "doctor", now.AddMinutes(1));
        Assert.Equal(MedicalRecordSource.Patient, a.Source);
        Assert.Equal("recorder", a.RecordedByStaffId);
        Assert.Equal("doctor", a.VerifiedByStaffId);
        p.SupersedeEntry(a, "doctor", now.AddMinutes(2));
        Assert.False(a.IsActive);
        Assert.Throws<InvalidOperationException>(() => p.VerifyEntry(a, "doctor", now));
    }

    [Fact]
    public void StaffVerificationIncludesAttribution()
    {
        var a = Profile().RecordMedication("synthetic", MedicationStatus.Past, MedicalRecordSource.Staff, "doctor", DateTimeOffset.UtcNow);
        Assert.Equal(ClinicalReviewStatus.Verified, a.ReviewStatus);
        Assert.Equal("doctor", a.VerifiedByStaffId);
        Assert.NotNull(a.VerifiedAtUtc);
    }

    [Fact]
    public void EntryFromAnotherProfileCannotBeVerifiedOrSuperseded()
    {
        var other = Profile().RecordAllergy("synthetic", null, null, MedicalRecordSource.Patient, null, DateTimeOffset.UtcNow);
        var target = Profile();
        Assert.Throws<InvalidOperationException>(() => target.VerifyEntry(other, "doctor", DateTimeOffset.UtcNow));
        Assert.Throws<InvalidOperationException>(() => target.SupersedeEntry(other, "doctor", DateTimeOffset.UtcNow));
        Assert.True(other.IsActive);
    }

    [Fact]
    public void CollectionsCannotBeMutatedOutsideAggregate()
    {
        var p = Profile();
        Assert.Throws<NotSupportedException>(() => ((ICollection<PatientAllergy>)p.Allergies).Clear());
    }

    [Fact]
    public void InvalidBasicsEnumIsRejected() =>
        Assert.Throws<ArgumentException>(() => Profile().UpdateBasics((BloodType)99, AllergyStatus.Unknown, null, null, "doctor", DateTimeOffset.UtcNow));

    private static PatientMedicalProfile Profile() => new(Guid.NewGuid(), "doctor", DateTimeOffset.UtcNow);
}
