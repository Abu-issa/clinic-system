using Clinic.Domain.Entities;
using Clinic.Domain.Enums;

namespace Clinic.UnitTests;

public sealed class VisitDomainTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 15, 10, 0, 0, TimeSpan.Zero);
    private static Visit Draft() => new(Guid.NewGuid(), Guid.NewGuid(), null, Now, "synthetic-staff", Now);

    [Fact]
    public void WalkInStartsIncompleteAndDraftIsEditable()
    {
        var v = Draft();
        Assert.Null(v.AppointmentId); Assert.Null(v.Diagnosis); Assert.Null(v.FinalizedAtUtc);
        Assert.Equal(VisitStatus.Draft, v.Status);
        v.UpdateDraft(new(ChiefComplaint: " Synthetic complaint ", InternalNotes: "staff only", PatientSummary: "summary"), "editor", Now.AddMinutes(1));
        Assert.Equal("Synthetic complaint", v.ChiefComplaint); Assert.Equal("staff only", v.InternalNotes);
        Assert.Equal("summary", v.PatientSummary); Assert.Equal("editor", v.LastModifiedByStaffId);
    }

    [Fact]
    public void FinalizationFreezesClinicalContentAndVitals()
    {
        var v = Draft(); v.UpdateDraft(new(Diagnosis: "Synthetic diagnosis"), "doctor", Now);
        v.FinalizeVisit("finalizer", Now.AddMinutes(1));
        Assert.Equal("finalizer", v.FinalizedByStaffId); Assert.Equal(Now.AddMinutes(1), v.FinalizedAtUtc);
        Assert.Throws<InvalidOperationException>(() => v.UpdateDraft(new(Diagnosis: "replacement"), "doctor", Now));
        Assert.Throws<InvalidOperationException>(() => v.AddVital(new VitalReading.Weight(70), Now, "doctor", Now));
        Assert.Throws<InvalidOperationException>(() => v.FinalizeVisit("doctor", Now));
        Assert.Equal("Synthetic diagnosis", v.Diagnosis); Assert.Empty(v.VitalMeasurements);
    }

    [Theory]
    [InlineData("", "content")]
    [InlineData("reason", " ")]
    [InlineData(null, "content")]
    [InlineData("reason", null)]
    public void AmendmentRequiresReasonAndContentWithoutPartialMutation(string? reason, string? content)
    {
        var v = Draft(); v.FinalizeVisit("doctor", Now);
        Assert.Throws<ArgumentException>(() => v.AddAmendment(reason!, content!, "other", Now.AddHours(1)));
        Assert.Empty(v.Amendments); Assert.Equal("doctor", v.LastModifiedByStaffId);
    }

    [Fact]
    public void AmendmentsAppendOnlyAfterFinalization()
    {
        var v = Draft();
        Assert.Throws<InvalidOperationException>(() => v.AddAmendment("reason", "text", "doctor", Now));
        v.FinalizeVisit("doctor", Now);
        v.AddAmendment("correction", "first", "doctor-a", Now.AddMinutes(1));
        var first = v.Amendments.Single();
        v.AddAmendment("addition", "second", "doctor-b", Now.AddMinutes(2));
        Assert.Equal(2, v.Amendments.Count); Assert.Equal("first", first.AmendmentText);
        Assert.Equal("doctor-a", first.CreatedByStaffId); Assert.Equal(v.Id, first.VisitId);
        Assert.Equal(Now, v.FinalizedAtUtc); Assert.Null(v.Diagnosis);
        Assert.Throws<NotSupportedException>(() => ((ICollection<VisitAmendment>)v.Amendments).Clear());
    }

    public static IEnumerable<object[]> InvalidVitals()
    {
        yield return [new VitalReading.BloodPressure(-1, 80)];
        yield return [new VitalReading.BloodPressure(120, -1)];
        yield return [new VitalReading.HeartRate(-1)];
        yield return [new VitalReading.OxygenSaturation(-1)];
        yield return [new VitalReading.OxygenSaturation(101)];
        yield return [new VitalReading.Weight(-1)];
        yield return [new VitalReading.Height(-1)];
        yield return [new VitalReading.RespiratoryRate(0)];
        yield return [new VitalReading.RespiratoryRate(-1)];
        yield return [new VitalReading.Temperature(36.1234m)];
    }

    [Theory]
    [MemberData(nameof(InvalidVitals))]
    public void StructurallyInvalidVitalsCannotMutateAggregate(VitalReading reading)
    {
        var v = Draft();
        Assert.Throws<ArgumentException>(() => v.AddVital(reading, Now, "doctor", Now.AddMinutes(1)));
        Assert.Empty(v.VitalMeasurements); Assert.Equal(Now, v.LastModifiedAtUtc);
    }

    [Fact]
    public void NumericValidationDoesNotImposeNormalRanges()
    {
        var v = Draft();
        v.AddVital(new VitalReading.Temperature(-2), Now, "doctor", Now);
        v.AddVital(new VitalReading.HeartRate(0), Now, "doctor", Now);
        v.AddVital(new VitalReading.BloodPressure(50, 100), Now, "doctor", Now);
        Assert.Equal(3, v.VitalMeasurements.Count);
        Assert.Equal(VitalUnit.Celsius, v.VitalMeasurements.First().Unit);
    }

    [Fact]
    public void InvalidDraftEditDoesNotPartiallyChangeContent()
    {
        var v = Draft(); v.UpdateDraft(new(ChiefComplaint: "original"), "doctor", Now);
        Assert.Throws<ArgumentException>(() => v.UpdateDraft(new(ChiefComplaint: "changed", PatientSummary: " "), "doctor", Now));
        Assert.Equal("original", v.ChiefComplaint);
        Assert.Throws<ArgumentException>(() => v.FinalizeVisit(" ", Now));
        Assert.Equal(VisitStatus.Draft, v.Status);
    }

    [Fact]
    public void RespiratoryRateValidatesPositiveBreathsPerMinute()
    {
        var v = Draft();
        v.AddVital(new VitalReading.RespiratoryRate(16), Now, "doctor", Now);
        var vital = Assert.Single(v.VitalMeasurements);
        Assert.Equal(VitalMeasurementType.RespiratoryRate, vital.Type);
        Assert.Equal(VitalUnit.BreathsPerMin, vital.Unit);
        Assert.Equal(16, vital.RespiratoryRateBreathsPerMin);
        Assert.Null(vital.SystolicMmHg);
        Assert.Null(vital.DiastolicMmHg);
        Assert.Null(vital.HeartRateBpm);
        Assert.Null(vital.TemperatureCelsius);
        Assert.Null(vital.OxygenSaturationPercent);
        Assert.Null(vital.WeightKg);
        Assert.Null(vital.HeightCm);
    }
}
