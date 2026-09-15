using Clinic.Domain.Enums;

namespace Clinic.Domain.Entities;

// Closed, typed inputs prevent caller-selected units or malformed value pairs.
public abstract record VitalReading
{
    private VitalReading() { }
    public sealed record BloodPressure(int Systolic, int Diastolic) : VitalReading;
    public sealed record HeartRate(int Bpm) : VitalReading;
    public sealed record Temperature(decimal Celsius) : VitalReading;
    public sealed record OxygenSaturation(decimal Percent) : VitalReading;
    public sealed record Weight(decimal Kg) : VitalReading;
    public sealed record Height(decimal Cm) : VitalReading;
    public sealed record RespiratoryRate(int BreathsPerMinute) : VitalReading;
}

public sealed class VitalMeasurement
{
    private VitalMeasurement() { }
    internal VitalMeasurement(Guid visitId, VitalReading reading, DateTimeOffset measuredAt, string actor, DateTimeOffset now)
    {
        Visit.Actor(actor);
        switch (reading)
        {
            case VitalReading.BloodPressure p when p.Systolic >= 0 && p.Diastolic >= 0:
                Type = VitalMeasurementType.BloodPressure; Unit = VitalUnit.MmHg;
                SystolicMmHg = p.Systolic; DiastolicMmHg = p.Diastolic; break;
            case VitalReading.HeartRate h when h.Bpm >= 0:
                Type = VitalMeasurementType.HeartRate; Unit = VitalUnit.Bpm; HeartRateBpm = h.Bpm; break;
            case VitalReading.Temperature t:
                Type = VitalMeasurementType.Temperature; Unit = VitalUnit.Celsius; TemperatureCelsius = Number(t.Celsius, true); break;
            case VitalReading.OxygenSaturation o when o.Percent <= 100:
                Type = VitalMeasurementType.OxygenSaturation; Unit = VitalUnit.Percent; OxygenSaturationPercent = Number(o.Percent); break;
            case VitalReading.Weight w:
                Type = VitalMeasurementType.Weight; Unit = VitalUnit.Kg; WeightKg = Number(w.Kg); break;
            case VitalReading.Height h:
                Type = VitalMeasurementType.Height; Unit = VitalUnit.Cm; HeightCm = Number(h.Cm); break;
            case VitalReading.RespiratoryRate r when r.BreathsPerMinute > 0:
                Type = VitalMeasurementType.RespiratoryRate; Unit = VitalUnit.BreathsPerMin; RespiratoryRateBreathsPerMin = r.BreathsPerMinute; break;
            default: throw new ArgumentException("Invalid vital reading.");
        }
        Id = Guid.NewGuid(); VisitId = visitId; MeasuredAtUtc = measuredAt.ToUniversalTime();
        CreatedAtUtc = now.ToUniversalTime(); CreatedByStaffId = actor;
    }
    // Storage precision, not a clinical normal range. Negative Celsius is meaningful.
    private static decimal Number(decimal value, bool signed = false)
    {
        if ((!signed && value < 0) || value < -9999999.999m || value > 9999999.999m || decimal.Round(value, 3) != value)
            throw new ArgumentException("Reading is outside the supported numeric representation.");
        return value;
    }
    public Guid Id { get; private set; }
    public Guid VisitId { get; private set; }
    public VitalMeasurementType Type { get; private set; }
    public VitalUnit Unit { get; private set; }
    public int? SystolicMmHg { get; private set; }
    public int? DiastolicMmHg { get; private set; }
    public int? HeartRateBpm { get; private set; }
    public decimal? TemperatureCelsius { get; private set; }
    public decimal? OxygenSaturationPercent { get; private set; }
    public decimal? WeightKg { get; private set; }
    public decimal? HeightCm { get; private set; }
    public int? RespiratoryRateBreathsPerMin { get; private set; }
    public DateTimeOffset MeasuredAtUtc { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public string CreatedByStaffId { get; private set; } = null!;
}
