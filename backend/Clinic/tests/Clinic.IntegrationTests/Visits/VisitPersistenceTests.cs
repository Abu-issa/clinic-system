using Clinic.Application.Visits;
using Clinic.Domain.Entities;
using Clinic.Domain.Enums;
using Clinic.Infrastructure.Persistence;
using Clinic.Infrastructure.Repositories;
using Clinic.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Clinic.IntegrationTests.Visits;

public sealed class VisitPersistenceTests(SqlDatabaseFixture database) : IClassFixture<SqlDatabaseFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 9, 15, 10, 0, 0, TimeSpan.Zero);
    private sealed class FixedClock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }
    private static VisitService Service(ClinicDbContext db) => new(new VisitStore(db), new FixedClock());

    private static async Task<(Patient Patient, Doctor Doctor, Appointment Appointment)> Seed(ClinicDbContext db)
    {
        var patient = new Patient("Synthetic visit patient", "shared");
        var doctor = new Doctor("Synthetic visit doctor");
        var appointment = new Appointment(patient.Id, doctor.Id, Now, Now.AddMinutes(30));
        db.AddRange(patient, doctor, appointment); await db.SaveChangesAsync();
        return (patient, doctor, appointment);
    }
    private static async Task<VisitDetails> Draft(ClinicDbContext db)
    {
        var s = await Seed(db);
        var result = await Service(db).CreateAsync(new(s.Patient.Id, s.Doctor.Id, null, Now), "synthetic-doctor");
        Assert.True(result.IsSuccess, result.Error.ToString()); return result.Details!;
    }

    [Fact]
    public async Task WalkInAndLinkedVisitsUseClockAndDoNotChangeAppointment()
    {
        await using var db = database.CreateContext(); var seed = await Seed(db); var service = Service(db);
        var appointmentVersion = seed.Appointment.RowVersion.ToArray();
        foreach (var link in new Guid?[] { null, seed.Appointment.Id })
        {
            var v = (await service.CreateAsync(new(seed.Patient.Id, seed.Doctor.Id, link, Now.ToOffset(TimeSpan.FromHours(3))), "synthetic-doctor")).Details!;
            Assert.Equal(link, v.AppointmentId); Assert.Equal(VisitStatus.Draft, v.Status);
            Assert.Equal(Now, v.CreatedAtUtc); Assert.Equal(TimeSpan.Zero, v.OccurredAtUtc.Offset);
            Assert.Equal(8, v.RowVersion.Length); Assert.Null(v.Diagnosis);
            Assert.True((await service.FinalizeAsync(v.PatientId, v.Id, v.RowVersion, "synthetic-doctor")).IsSuccess);
        }
        await db.Entry(seed.Appointment).ReloadAsync();
        Assert.Equal(AppointmentStatus.Pending, seed.Appointment.Status); Assert.Equal(appointmentVersion, seed.Appointment.RowVersion);
        var list = await service.ListAsync(seed.Patient.Id); Assert.Equal(2, list.Visits.Count);
        Assert.False((await service.ListAsync(seed.Patient.Id, take: 101)).IsSuccess);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AppointmentPatientAndDoctorMismatchesAreRejected(bool patientMismatch)
    {
        await using var db = database.CreateContext(); var a = await Seed(db); var b = await Seed(db);
        var result = await Service(db).CreateAsync(new(patientMismatch ? b.Patient.Id : a.Patient.Id,
            patientMismatch ? a.Doctor.Id : b.Doctor.Id, a.Appointment.Id, Now), "doctor");
        Assert.Equal(VisitError.AppointmentMismatch, result.Error);
        Assert.False(await db.Set<Visit>().AnyAsync(x => x.AppointmentId == a.Appointment.Id));
    }

    [Fact]
    public async Task MissingReferencesAndCrossPatientLookupAreRejected()
    {
        await using var db = database.CreateContext(); var v = await Draft(db); var service = Service(db);
        Assert.Equal(VisitError.PatientNotFound, (await service.CreateAsync(new(Guid.NewGuid(), v.DoctorId, null, Now), "doctor")).Error);
        Assert.Equal(VisitError.DoctorNotFound, (await service.CreateAsync(new(v.PatientId, Guid.NewGuid(), null, Now), "doctor")).Error);
        Assert.Equal(VisitError.AppointmentNotFound, (await service.CreateAsync(new(v.PatientId, v.DoctorId, Guid.NewGuid(), Now), "doctor")).Error);
        Assert.Equal(VisitError.VisitNotFound, (await service.GetAsync(Guid.NewGuid(), v.Id)).Error);
        Assert.Equal(VisitError.VisitNotFound, (await service.FinalizeAsync(Guid.NewGuid(), v.Id, v.RowVersion, "doctor")).Error);
        Assert.Equal(VisitError.InvalidRowVersion, (await service.FinalizeAsync(v.PatientId, v.Id, [], "doctor")).Error);
    }

    [Fact]
    public async Task AllVitalTypesRoundTripWithFixedUnitsAndPreservedHistory()
    {
        VisitDetails v;
        await using (var db = database.CreateContext())
        {
            v = await Draft(db);
            VitalReading[] readings = [new VitalReading.BloodPressure(120, 80), new VitalReading.HeartRate(72),
                new VitalReading.Temperature(36.75m), new VitalReading.OxygenSaturation(98.5m),
                new VitalReading.Weight(70.125m), new VitalReading.Height(175.5m), new VitalReading.RespiratoryRate(18), new VitalReading.Weight(71)];
            foreach (var reading in readings)
            {
                var result = await Service(db).AddVitalAsync(v.PatientId, v.Id, new(reading, Now.AddMinutes(-5), v.RowVersion), "nurse");
                Assert.True(result.IsSuccess, result.Error.ToString());
                Assert.NotEqual(v.RowVersion, result.Details!.RowVersion); v = result.Details;
            }
        }
        await using var verify = database.CreateContext();
        var saved = (await Service(verify).GetAsync(v.PatientId, v.Id)).Details!;
        Assert.Equal(8, saved.VitalMeasurements.Count);
        var bp = Assert.Single(saved.VitalMeasurements, x => x.Type == VitalMeasurementType.BloodPressure);
        Assert.Equal(120, bp.SystolicMmHg); Assert.Equal(80, bp.DiastolicMmHg); Assert.Equal(VitalUnit.MmHg, bp.Unit);
        Assert.Equal(72, Assert.Single(saved.VitalMeasurements, x => x.Unit == VitalUnit.Bpm).HeartRateBpm);
        Assert.Equal(36.75m, Assert.Single(saved.VitalMeasurements, x => x.Unit == VitalUnit.Celsius).TemperatureCelsius);
        Assert.Equal(98.5m, Assert.Single(saved.VitalMeasurements, x => x.Unit == VitalUnit.Percent).OxygenSaturationPercent);
        Assert.Equal(175.5m, Assert.Single(saved.VitalMeasurements, x => x.Unit == VitalUnit.Cm).HeightCm);
        Assert.Equal(18, Assert.Single(saved.VitalMeasurements, x => x.Unit == VitalUnit.BreathsPerMin).RespiratoryRateBreathsPerMin);
        Assert.Equal(new decimal?[] { 70.125m, 71 }, saved.VitalMeasurements.Where(x => x.Unit == VitalUnit.Kg).Select(x => x.WeightKg).Order().ToArray());
        Assert.All(saved.VitalMeasurements, x => { Assert.Equal("nurse", x.CreatedByStaffId); Assert.Equal(Now.AddMinutes(-5), x.MeasuredAtUtc); Assert.Equal(Now, x.CreatedAtUtc); });
    }

    [Fact]
    public async Task DraftEditsFinalizeAndAmendmentsRoundTripWithoutOverwritingOriginal()
    {
        await using var db = database.CreateContext(); var v = await Draft(db); var service = Service(db);
        v = (await service.UpdateAsync(v.PatientId, v.Id, new(new(Diagnosis: "Synthetic diagnosis", InternalNotes: "internal", PatientSummary: "summary"), v.RowVersion), "doctor")).Details!;
        v = (await service.FinalizeAsync(v.PatientId, v.Id, v.RowVersion, "finalizer")).Details!;
        Assert.Equal(VisitError.InvalidLifecycle, (await service.UpdateAsync(v.PatientId, v.Id, new(new(Diagnosis: "replacement"), v.RowVersion), "doctor")).Error);
        Assert.Equal(VisitError.InvalidLifecycle, (await service.AddVitalAsync(v.PatientId, v.Id, new(new VitalReading.Weight(70), Now, v.RowVersion), "doctor")).Error);
        foreach (var text in new[] { "first", "second" })
            v = (await service.AddAmendmentAsync(v.PatientId, v.Id, new("correction", text, v.RowVersion), "amender")).Details!;
        await using var verify = database.CreateContext(); var saved = (await Service(verify).GetAsync(v.PatientId, v.Id)).Details!;
        Assert.Equal("Synthetic diagnosis", saved.Diagnosis); Assert.Equal("internal", saved.InternalNotes); Assert.Equal("summary", saved.PatientSummary);
        Assert.Equal(2, saved.Amendments.Count); Assert.Contains(saved.Amendments, x => x.AmendmentText == "first");
        Assert.Equal("finalizer", saved.FinalizedByStaffId); Assert.Equal(Now, saved.FinalizedAtUtc);
        Assert.All(saved.Amendments, x => Assert.Equal("amender", x.CreatedByStaffId));
        Assert.Equal(v.RowVersion, saved.RowVersion);
    }

    [Fact]
    public async Task FinalizationRaceRejectsStaleVitalAndRollsBackChildren()
    {
        VisitDetails v; await using (var seed = database.CreateContext()) v = await Draft(seed);
        await using var a = database.CreateContext(); await using var b = database.CreateContext();
        await new VisitStore(a).GetAsync(v.PatientId, v.Id, default);
        await new VisitStore(b).GetAsync(v.PatientId, v.Id, default);
        Assert.True((await Service(a).FinalizeAsync(v.PatientId, v.Id, v.RowVersion, "winner")).IsSuccess);
        Assert.Equal(VisitError.VisitChanged, (await Service(b).AddVitalAsync(v.PatientId, v.Id, new(new VitalReading.Weight(70), Now, v.RowVersion), "loser")).Error);
        // A later unrelated unit-of-work save must not retry the rejected child insert.
        b.Add(new Doctor("Synthetic later save")); await b.SaveChangesAsync();
        await using var verify = database.CreateContext(); var saved = (await Service(verify).GetAsync(v.PatientId, v.Id)).Details!;
        Assert.Equal(VisitStatus.Finalized, saved.Status); Assert.Empty(saved.VitalMeasurements);
        Assert.Equal("winner", saved.FinalizedByStaffId);
        Assert.Equal(VisitError.VisitChanged, (await Service(verify).UpdateAsync(v.PatientId, v.Id, new(new(Diagnosis: "stale"), v.RowVersion), "loser")).Error);
    }

    [Fact]
    public async Task CompetingFinalizationsHaveOneWinnerAndStaleAmendmentsAreAtomic()
    {
        VisitDetails v; await using (var seed = database.CreateContext()) v = await Draft(seed);
        await using var a = database.CreateContext(); await using var b = database.CreateContext();
        await new VisitStore(a).GetAsync(v.PatientId, v.Id, default); await new VisitStore(b).GetAsync(v.PatientId, v.Id, default);
        var results = await Task.WhenAll(Service(a).FinalizeAsync(v.PatientId, v.Id, v.RowVersion, "a"), Service(b).FinalizeAsync(v.PatientId, v.Id, v.RowVersion, "b"));
        v = Assert.Single(results, x => x.IsSuccess).Details!; Assert.Equal(VisitError.VisitChanged, Assert.Single(results, x => !x.IsSuccess).Error);
        await using var c = database.CreateContext(); await using var d = database.CreateContext();
        await new VisitStore(c).GetAsync(v.PatientId, v.Id, default); await new VisitStore(d).GetAsync(v.PatientId, v.Id, default);
        var amendments = await Task.WhenAll(Service(c).AddAmendmentAsync(v.PatientId, v.Id, new("reason", "a", v.RowVersion), "a"),
            Service(d).AddAmendmentAsync(v.PatientId, v.Id, new("reason", "b", v.RowVersion), "b"));
        Assert.Single(amendments, x => x.IsSuccess); Assert.Equal(VisitError.VisitChanged, Assert.Single(amendments, x => !x.IsSuccess).Error);
        await c.SaveChangesAsync(); await d.SaveChangesAsync();
        await using var verify = database.CreateContext(); Assert.Single((await Service(verify).GetAsync(v.PatientId, v.Id)).Details!.Amendments);
    }

    [Fact]
    public async Task SqlRowVersionRejectsCompetingDraftEditsAndStaleFinalization()
    {
        VisitDetails v; await using (var seed = database.CreateContext()) v = await Draft(seed);
        await using var a = database.CreateContext(); await using var b = database.CreateContext();
        await new VisitStore(a).GetAsync(v.PatientId, v.Id, default); await new VisitStore(b).GetAsync(v.PatientId, v.Id, default);
        var winner = await Service(a).UpdateAsync(v.PatientId, v.Id, new(new(Diagnosis: "winner"), v.RowVersion), "doctor-a");
        Assert.True(winner.IsSuccess); Assert.NotEqual(v.RowVersion, winner.Details!.RowVersion);
        Assert.Equal(VisitError.VisitChanged, (await Service(b).UpdateAsync(v.PatientId, v.Id, new(new(Diagnosis: "loser"), v.RowVersion), "doctor-b")).Error);
        Assert.Equal(VisitError.VisitChanged, (await Service(b).FinalizeAsync(v.PatientId, v.Id, v.RowVersion, "doctor-b")).Error);
        await b.SaveChangesAsync();
        await using var verify = database.CreateContext(); var saved = (await Service(verify).GetAsync(v.PatientId, v.Id)).Details!;
        Assert.Equal("winner", saved.Diagnosis); Assert.Equal(VisitStatus.Draft, saved.Status);
        Assert.Equal(winner.Details.RowVersion, saved.RowVersion);
    }

    [Fact]
    public async Task InvalidCommandLeavesUnitOfWorkSafe()
    {
        await using var db = database.CreateContext(); var v = await Draft(db);
        Assert.Equal(VisitError.InvalidInput, (await Service(db).UpdateAsync(v.PatientId, v.Id,
            new(new(ChiefComplaint: "partial", PatientSummary: " "), v.RowVersion), "doctor")).Error);
        await db.SaveChangesAsync();
        var saved = (await Service(db).GetAsync(v.PatientId, v.Id)).Details!;
        Assert.Null(saved.ChiefComplaint); Assert.Equal(v.RowVersion, saved.RowVersion);
    }

    [Fact]
    public async Task RowVersionAndRestrictDeleteAreEnforcedBySql()
    {
        await using var db = database.CreateContext(); var v = await Draft(db);
        var rowVersion = db.Model.FindEntityType(typeof(Visit))!.FindProperty(nameof(Visit.RowVersion))!;
        Assert.True(rowVersion.IsConcurrencyToken); Assert.Equal(ValueGenerated.OnAddOrUpdate, rowVersion.ValueGenerated);
        Assert.Equal("rowversion", rowVersion.GetColumnType());
        v = (await Service(db).AddVitalAsync(v.PatientId, v.Id, new(new VitalReading.HeartRate(70), Now, v.RowVersion), "doctor")).Details!;
        await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM Visits WHERE Id = {v.Id}"));
        await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM Patients WHERE Id = {v.PatientId}"));
        // A type/unit substitution or NULL numeric payload cannot pass the relational shape check.
        await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE VitalMeasurements SET Unit = 4 WHERE VisitId = {v.Id}"));
        await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE VitalMeasurements SET HeartRateBpm = NULL WHERE VisitId = {v.Id}"));
    }

    [Fact]
    public async Task MigrationPreservesPatientAppointmentMedicalProfileAndIdentity()
    {
        var isolated = new SqlDatabaseFixture();
        try
        {
            await using var db = isolated.CreateContext(); var migrator = db.GetService<IMigrator>();
            await migrator.MigrateAsync("20260915105237_AddPatientMedicalRecordFoundation");
            var seed = await Seed(db);
            var profile = new PatientMedicalProfile(seed.Patient.Id, "synthetic-doctor", Now);
            profile.RecordAllergy("Synthetic allergen", null, null, MedicalRecordSource.Patient, "synthetic-doctor", Now);
            db.Add(profile);
            var staff = new Clinic.Infrastructure.Authentication.StaffUser { Id = Guid.NewGuid().ToString(), UserName = "synthetic-migration", NormalizedUserName = "SYNTHETIC-MIGRATION", SecurityStamp = Guid.NewGuid().ToString() };
            db.Users.Add(staff); await db.SaveChangesAsync();
            var patientVersion = seed.Patient.RowVersion.ToArray(); var appointmentVersion = seed.Appointment.RowVersion.ToArray(); var profileVersion = profile.RowVersion.ToArray();
            await migrator.MigrateAsync(); db.ChangeTracker.Clear();
            var patient = await db.Patients.SingleAsync(x => x.Id == seed.Patient.Id);
            var appointment = await db.Appointments.SingleAsync(x => x.Id == seed.Appointment.Id);
            var savedProfile = await db.Set<PatientMedicalProfile>().Include(x => x.Allergies).SingleAsync(x => x.Id == profile.Id);
            Assert.Null(patient.MedicalRecordNumber); Assert.Equal(seed.Patient.FullName, patient.FullName); Assert.Equal(patientVersion, patient.RowVersion);
            Assert.Equal(patient.Id, appointment.PatientId); Assert.Equal(seed.Doctor.Id, appointment.DoctorId); Assert.Equal(appointmentVersion, appointment.RowVersion);
            Assert.Equal(seed.Appointment.StartsAtUtc, appointment.StartsAtUtc); Assert.Equal(seed.Appointment.EndsAtUtc, appointment.EndsAtUtc);
            Assert.Equal(profileVersion, savedProfile.RowVersion); Assert.Equal("Synthetic allergen", Assert.Single(savedProfile.Allergies).Substance);
            Assert.Equal(staff.SecurityStamp, (await db.Users.SingleAsync(x => x.Id == staff.Id)).SecurityStamp);
            Assert.False(await db.Set<Visit>().AnyAsync()); Assert.False(await db.Set<VitalMeasurement>().AnyAsync()); Assert.False(await db.Set<VisitAmendment>().AnyAsync());
        }
        finally { await isolated.DisposeAsync(); }
    }
}
