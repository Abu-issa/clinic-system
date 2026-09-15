using Clinic.Application.Patients;
using Clinic.Domain.Entities;
using Clinic.Domain.Enums;
using Clinic.Infrastructure.Repositories;
using Clinic.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Clinic.IntegrationTests.Patients;

public sealed class PatientRecordsTests : IClassFixture<SqlDatabaseFixture>
{
    private readonly SqlDatabaseFixture database;
    public PatientRecordsTests(SqlDatabaseFixture database) => this.database = database;
    private static CreatePatientRequest NewPatient(string mrn) => new("Synthetic", "shared-phone", null, mrn, "PAPER", null, null, null, null);
    private PatientRecordsService Service(Clinic.Infrastructure.Persistence.ClinicDbContext db) => new(new PatientRecordsStore(db), TimeProvider.System);

    [Fact]
    public async Task CreationRequiresMrnButNoAccountAndAllowsSharedPhone()
    {
        await using var db = database.CreateContext();
        var service = Service(db);
        var accounts = await db.Users.CountAsync();
        Assert.False((await service.CreateAsync(NewPatient(" "))).IsSuccess);
        var mrn = Guid.NewGuid().ToString("N");
        var a = await service.CreateAsync(NewPatient(mrn));
        var b = await service.CreateAsync(NewPatient(Guid.NewGuid().ToString("N")));
        Assert.True(a.IsSuccess); Assert.True(b.IsSuccess);
        Assert.Equal("PAPER", a.Details!.LegacyPaperFileNumber);
        Assert.Equal(accounts, await db.Users.CountAsync());
        Assert.Equal(a.Details.PhoneNumber, b.Details!.PhoneNumber);
        await using var duplicate = database.CreateContext();
        Assert.Equal(PatientAdminError.MedicalRecordNumberAlreadyExists, (await Service(duplicate).CreateAsync(NewPatient(mrn))).Error);
        await using var legacy = database.CreateContext();
        legacy.AddRange(new Patient("legacy-a", "shared-phone"), new Patient("legacy-b", "shared-phone"));
        await legacy.SaveChangesAsync();
        Assert.True(await legacy.Patients.CountAsync(x => x.MedicalRecordNumber == null) >= 2);
    }

    [Fact]
    public async Task StructuredEntriesRoundTripAndSupersessionRetainsHistory()
    {
        Guid id;
        MedicalProfileDetails initial;
        await using (var db = database.CreateContext())
        {
            id = (await Service(db).CreateAsync(NewPatient(Guid.NewGuid().ToString("N")))).Details!.PatientId;
            var result = await Service(db).SaveProfileAsync(id, Snapshot(), "doctor");
            Assert.True(result.IsSuccess, result.Error.ToString()); initial = result.Details!;
        }
        await using (var db = database.CreateContext())
        {
            var profile = (await Service(db).GetProfileAsync(id)).Details!;
            Assert.Single(profile.Allergies); Assert.Single(profile.ChronicConditions);
            Assert.Single(profile.Medications); Assert.Single(profile.Surgeries); Assert.Single(profile.FamilyHistory);
            Assert.Equal(ClinicalReviewStatus.PendingReview, profile.Allergies[0].ReviewStatus);
            Assert.Equal(MedicationStatus.Past, profile.Medications[0].Status);
            var result = await Service(db).SaveProfileAsync(id, ReplaceAll(initial), "doctor");
            Assert.True(result.IsSuccess, result.Error.ToString());
            Assert.NotEqual(initial.RowVersion, result.Details!.RowVersion);
        }
        await using (var db = database.CreateContext())
        {
            var rows = await db.Set<PatientAllergy>().Where(x => x.ProfileId == db.Set<PatientMedicalProfile>().Where(p => p.PatientId == id).Select(p => p.Id).Single()).ToListAsync();
            Assert.Equal(2, rows.Count);
            Assert.Single(rows, x => x.IsActive);
            Assert.Equal("doctor", rows.Single(x => !x.IsActive).SupersededByStaffId);
        }
    }

    [Fact]
    public async Task ReviewAndPatientContentCannotDowngradeVerifiedEntry()
    {
        Guid id; MedicalProfileDetails details;
        await using (var db = database.CreateContext())
        {
            id = (await Service(db).CreateAsync(NewPatient(Guid.NewGuid().ToString("N")))).Details!.PatientId;
            details = (await Service(db).SaveProfileAsync(id, Snapshot(), "doctor")).Details!;
        }
        await using (var db = database.CreateContext())
        {
            var request = Retain(details) with {
                Allergies = [new(details.Allergies[0].Id, "Synthetic allergen", null, AllergySeverity.Mild, MedicalRecordSource.Patient, ClinicalReviewStatus.Verified)] };
            details = (await Service(db).SaveProfileAsync(id, request, "reviewer")).Details!;
            Assert.NotNull(details.Allergies[0].VerifiedAtUtc);
        }
        await using (var db = database.CreateContext())
        {
            var request = Retain(details) with {
                Allergies = [new(details.Allergies[0].Id, "Changed patient report", null, null, MedicalRecordSource.Patient, ClinicalReviewStatus.PendingReview)] };
            Assert.Equal(MedicalProfileError.EntryCannotBeUnverified, (await Service(db).SaveProfileAsync(id, request, "doctor")).Error);
        }
        await using (var db = database.CreateContext())
        {
            var current = (await Service(db).GetProfileAsync(id)).Details!;
            Assert.Equal("Synthetic allergen", current.Allergies[0].Substance);
            Assert.Equal(details.RowVersion, current.RowVersion);
        }
    }

    [Fact]
    public async Task CrossPatientEntryReferenceAndStaleProfileAreRejected()
    {
        await using var db = database.CreateContext();
        var service = Service(db);
        var a = (await service.CreateAsync(NewPatient(Guid.NewGuid().ToString("N")))).Details!.PatientId;
        var b = (await service.CreateAsync(NewPatient(Guid.NewGuid().ToString("N")))).Details!.PatientId;
        var pa = (await service.SaveProfileAsync(a, Snapshot(), "doctor")).Details!;
        Assert.Equal(MedicalProfileError.InvalidEntryReference, (await service.SaveProfileAsync(b, Snapshot() with {
            Allergies = [new(pa.Allergies[0].Id, "Synthetic", null, null, MedicalRecordSource.Patient, ClinicalReviewStatus.PendingReview)] }, "doctor")).Error);
        Assert.True((await service.SaveProfileAsync(a, ReplaceAll(pa), "doctor")).IsSuccess);
        Assert.Equal(MedicalProfileError.ProfileChanged, (await service.SaveProfileAsync(a, ReplaceAll(pa), "doctor")).Error);
    }

    [Fact]
    public async Task StaleAdministrativeUpdateDoesNotOverwrite()
    {
        await using var db = database.CreateContext();
        var service = Service(db);
        var p = (await service.CreateAsync(NewPatient(Guid.NewGuid().ToString("N")))).Details!;
        var update = new UpdatePatientRequest("Updated", "shared-phone", null, null, null, null, p.RowVersion);
        Assert.True((await service.UpdateAsync(p.PatientId, update)).IsSuccess);
        Assert.Equal(PatientAdminError.PatientChanged, (await service.UpdateAsync(p.PatientId, update with { FullName = "Stale" })).Error);
        Assert.Equal("Updated", (await service.GetAsync(p.PatientId)).Details!.FullName);
    }

    [Fact]
    public async Task ProfileRaceRollsBackAllChildChanges()
    {
        Guid id; MedicalProfileDetails original;
        await using (var db = database.CreateContext())
        {
            id = (await Service(db).CreateAsync(NewPatient(Guid.NewGuid().ToString("N")))).Details!.PatientId;
            original = (await Service(db).SaveProfileAsync(id, Snapshot(), "doctor")).Details!;
        }
        await using var a = database.CreateContext();
        await using var b = database.CreateContext();
        // Preload both tracked versions so the loser reaches the actual SQL row-version check.
        await new PatientRecordsStore(a).ProfileAsync(id, default);
        await new PatientRecordsStore(b).ProfileAsync(id, default);
        Assert.True((await Service(a).SaveProfileAsync(id, ReplaceAll(original), "doctor")).IsSuccess);
        Assert.Equal(MedicalProfileError.ProfileChanged, (await Service(b).SaveProfileAsync(id, ReplaceAll(original), "doctor")).Error);
        await using var verify = database.CreateContext();
        var profile = await new PatientRecordsStore(verify).ProfileAsync(id, default);
        Assert.Equal(2, profile!.Allergies.Count);
        Assert.Single(profile.Allergies, x => x.IsActive);
    }

    [Fact]
    public async Task MigrationPreservesLegacyPatientAndAppointment()
    {
        // Separate fixture so downgrade cannot affect this class's other tests.
        var isolated = new SqlDatabaseFixture();
        await isolated.InitializeAsync();
        try
        {
            await using var db = isolated.CreateContext();
            var migrator = db.GetService<IMigrator>();
            await migrator.MigrateAsync("20260914205919_AddStaffIdentity");
            var patient = Guid.NewGuid(); var doctor = new Doctor("Synthetic migration doctor");
            db.Add(doctor); await db.SaveChangesAsync();
            var time = DateTimeOffset.UtcNow;
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO Patients (Id, FullName, PhoneNumber, CreatedAtUtc) VALUES ({patient}, {"Legacy synthetic"}, {"shared"}, {time})");
            var appointment = new Appointment(patient, doctor.Id, time.AddDays(1), time.AddDays(1).AddMinutes(47));
            db.Add(appointment); await db.SaveChangesAsync();
            var version = appointment.RowVersion.ToArray();
            await migrator.MigrateAsync();
            db.ChangeTracker.Clear();
            var saved = await db.Patients.SingleAsync(x => x.Id == patient);
            Assert.Null(saved.MedicalRecordNumber); Assert.Null(saved.LegacyPaperFileNumber);
            Assert.Equal("Legacy synthetic", saved.FullName); Assert.Equal(time, saved.CreatedAtUtc);
            Assert.Equal(8, saved.RowVersion.Length);
            var appt = await db.Appointments.SingleAsync(x => x.Id == appointment.Id);
            Assert.Equal(patient, appt.PatientId); Assert.Equal(version, appt.RowVersion);
            Assert.Equal(appointment.StartsAtUtc, appt.StartsAtUtc); Assert.Equal(appointment.EndsAtUtc, appt.EndsAtUtc);
            Assert.False(await db.Set<PatientMedicalProfile>().AnyAsync());
        }
        finally { await isolated.DisposeAsync(); }
    }

    [Fact]
    public async Task RejectedSnapshotCannotLeakPartialChangesIntoLaterSave()
    {
        await using var db = database.CreateContext();
        var service = Service(db);
        var id = (await service.CreateAsync(NewPatient(Guid.NewGuid().ToString("N")))).Details!.PatientId;
        var first = (await service.SaveProfileAsync(id, Snapshot(), "doctor")).Details!;
        var invalid = ReplaceAll(first) with {
            ExpectedRowVersion = first.RowVersion,
            ChronicConditions = [new(null, "", null, MedicalRecordSource.Staff, ClinicalReviewStatus.Verified)] };
        Assert.False((await service.SaveProfileAsync(id, invalid, "doctor")).IsSuccess);
        // A different successful command reuses the same context after rejection.
        Assert.True((await service.CreateAsync(NewPatient(Guid.NewGuid().ToString("N")))).IsSuccess);
        var current = (await service.GetProfileAsync(id)).Details!;
        Assert.Equal(first.RowVersion, current.RowVersion);
        Assert.Equal(first.Allergies[0].Id, current.Allergies[0].Id);
    }

    [Fact]
    public async Task NoKnownAllergiesCannotCoexistWithActiveEntriesAndDeletesAreRestricted()
    {
        await using var db = database.CreateContext();
        var service = Service(db);
        var id = (await service.CreateAsync(NewPatient(Guid.NewGuid().ToString("N")))).Details!.PatientId;
        Assert.Equal(MedicalProfileError.InconsistentAllergyStatus, (await service.SaveProfileAsync(id,
            Snapshot() with { AllergyStatus = AllergyStatus.NoKnownAllergies }, "doctor")).Error);
        Assert.False(await db.Set<PatientMedicalProfile>().AnyAsync(x => x.PatientId == id));
        Assert.True((await service.SaveProfileAsync(id, Snapshot(), "doctor")).IsSuccess);
        await using var delete = database.CreateContext();
        delete.Remove((await delete.Patients.SingleAsync(x => x.Id == id)));
        await Assert.ThrowsAsync<DbUpdateException>(() => delete.SaveChangesAsync());
    }
    // Explicitly request replacement when a test needs retained superseded history.
    public static SaveMedicalProfileRequest ReplaceAll(MedicalProfileDetails p) => Snapshot() with
    {
        ExpectedRowVersion = p.RowVersion,
        SupersededEntryIds = p.Allergies.Select(x => x.Id).Concat(p.ChronicConditions.Select(x => x.Id))
            .Concat(p.Medications.Select(x => x.Id)).Concat(p.Surgeries.Select(x => x.Id)).Concat(p.FamilyHistory.Select(x => x.Id)).ToArray()
    };

    public static SaveMedicalProfileRequest Retain(MedicalProfileDetails p) => new(p.BloodType, p.AllergyStatus, p.SmokingStatus, p.DiabetesType,
        p.Allergies.Select(x => new AllergyInput(x.Id, x.Substance, x.Reaction, x.Severity, x.Source, x.ReviewStatus)).ToArray(),
        p.ChronicConditions.Select(x => new ChronicConditionInput(x.Id, x.ConditionName, x.Notes, x.Source, x.ReviewStatus)).ToArray(),
        p.Medications.Select(x => new MedicationInput(x.Id, x.MedicationName, x.Status, x.Source, x.ReviewStatus)).ToArray(),
        p.Surgeries.Select(x => new SurgeryInput(x.Id, x.ProcedureName, x.PerformedOn, x.Source, x.ReviewStatus)).ToArray(),
        p.FamilyHistory.Select(x => new FamilyHistoryInput(x.Id, x.Relation, x.Condition, x.Source, x.ReviewStatus)).ToArray(), p.RowVersion);
    public static SaveMedicalProfileRequest Snapshot() => new(null, AllergyStatus.HasKnownAllergies, null, null,
        [new(null, "Synthetic allergen", null, AllergySeverity.Mild, MedicalRecordSource.Patient, ClinicalReviewStatus.PendingReview)],
        [new(null, "Synthetic condition", "History", MedicalRecordSource.Staff, ClinicalReviewStatus.Verified)],
        [new(null, "Synthetic medication", MedicationStatus.Past, MedicalRecordSource.Staff, ClinicalReviewStatus.Verified)],
        [new(null, "Synthetic surgery", new DateOnly(2020, 1, 1), MedicalRecordSource.Patient, ClinicalReviewStatus.PendingReview)],
        [new(null, "Parent", "Synthetic history", MedicalRecordSource.Patient, ClinicalReviewStatus.PendingReview)], null);
}
