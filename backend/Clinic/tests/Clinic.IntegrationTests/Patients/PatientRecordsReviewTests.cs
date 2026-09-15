using Clinic.Application.Patients;
using Clinic.Domain.Entities;
using Clinic.Domain.Enums;
using Clinic.Infrastructure.Persistence;
using Clinic.Infrastructure.Repositories;
using Clinic.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Clinic.IntegrationTests.Patients;

public sealed class PatientRecordsReviewTests : IClassFixture<SqlDatabaseFixture>
{
    private readonly SqlDatabaseFixture database;
    public PatientRecordsReviewTests(SqlDatabaseFixture database) => this.database = database;
    private static CreatePatientRequest Patient(string mrn) => new("Synthetic review", "shared", null, mrn, null, null, null, null, null);
    private static PatientRecordsService Service(ClinicDbContext db) => new(new PatientRecordsStore(db), TimeProvider.System);

    [Fact]
    public async Task Review_IncompleteCurrentSnapshotMustNotSupersedeVerifiedEntries()
    {
        await using var db = database.CreateContext();
        var service = Service(db);
        var id = (await service.CreateAsync(Patient(Guid.NewGuid().ToString("N")))).Details!.PatientId;
        var original = (await service.SaveProfileAsync(id, PatientRecordsTests.Snapshot(), "doctor")).Details!;
        var empty = new SaveMedicalProfileRequest(null, AllergyStatus.NoKnownAllergies, null, null, [], [], [], [], [], original.RowVersion);
        Assert.False((await service.SaveProfileAsync(id, empty, "doctor")).IsSuccess);
        await db.SaveChangesAsync();
        var current = (await service.GetProfileAsync(id)).Details!;
        Assert.Equal(original.RowVersion, current.RowVersion);
        Assert.Equal(original.ChronicConditions[0].Id, current.ChronicConditions[0].Id);
        Assert.Equal(ClinicalReviewStatus.Verified, current.ChronicConditions[0].ReviewStatus);
    }

    [Fact]
    public async Task Review_CancelledCreationMustNotBeFlushedByLaterSave()
    {
        await using var db = database.CreateContext();
        var mrn = Guid.NewGuid().ToString("N");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service(db).CreateAsync(Patient(mrn), cancellation.Token));
        await db.SaveChangesAsync();
        Assert.False(await db.Patients.AnyAsync(x => x.MedicalRecordNumber == mrn));
    }

    [Fact]
    public async Task Review_UnexpectedSaveFailureMustNotBeFlushedByLaterSave()
    {
        var fail = new FailingSave();
        var options = new DbContextOptionsBuilder<ClinicDbContext>().UseSqlServer(database.ConnectionString).AddInterceptors(fail).Options;
        await using var db = new ClinicDbContext(options);
        var mrn = Guid.NewGuid().ToString("N");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service(db).CreateAsync(Patient(mrn)));
        fail.Enabled = false;
        await db.SaveChangesAsync();
        Assert.False(await db.Patients.AnyAsync(x => x.MedicalRecordNumber == mrn));
    }

    [Fact]
    public async Task Review_ConcurrentMrnCreationHasOneWinnerAndControlledConflict()
    {
        var gate = new SaveGate();
        var options = new DbContextOptionsBuilder<ClinicDbContext>().UseSqlServer(database.ConnectionString).AddInterceptors(gate).Options;
        await using var a = new ClinicDbContext(options);
        await using var b = new ClinicDbContext(options);
        var mrn = Guid.NewGuid().ToString("N");
        var results = await Task.WhenAll(Service(a).CreateAsync(Patient(mrn)), Service(b).CreateAsync(Patient(mrn)));
        Assert.Single(results, x => x.IsSuccess);
        Assert.Single(results, x => x.Error == PatientAdminError.MedicalRecordNumberAlreadyExists);
        await using var verify = database.CreateContext();
        Assert.Equal(1, await verify.Patients.CountAsync(x => x.MedicalRecordNumber == mrn));
    }

    [Fact]
    public async Task Review_ExplicitSupersessionPreservesHistoryAndCannotReviveInactiveIds()
    {
        await using var db = database.CreateContext();
        var service = Service(db);
        var id = (await service.CreateAsync(Patient(Guid.NewGuid().ToString("N")))).Details!.PatientId;
        var original = (await service.SaveProfileAsync(id, PatientRecordsTests.Snapshot(), "recorder")).Details!;
        var removal = PatientRecordsTests.ReplaceAll(original) with {
            Allergies = [], ChronicConditions = [], Medications = [], Surgeries = [], FamilyHistory = [],
            AllergyStatus = AllergyStatus.NoKnownAllergies };
        var result = await service.SaveProfileAsync(id, removal, "reviewer");
        Assert.True(result.IsSuccess);
        Assert.Empty(result.Details!.Allergies);
        var history = await db.Set<PatientAllergy>().SingleAsync(x => x.Id == original.Allergies[0].Id);
        Assert.False(history.IsActive); Assert.Equal("recorder", history.RecordedByStaffId);
        Assert.Equal("reviewer", history.SupersededByStaffId); Assert.NotNull(history.SupersededAtUtc);
        var revive = PatientRecordsTests.Retain(original) with { ExpectedRowVersion = result.Details.RowVersion };
        Assert.Equal(MedicalProfileError.InvalidEntryReference, (await service.SaveProfileAsync(id, revive, "reviewer")).Error);
        var duplicateRemoval = PatientRecordsTests.Retain(result.Details) with { SupersededEntryIds = [history.Id] };
        Assert.Equal(MedicalProfileError.InvalidEntryReference, (await service.SaveProfileAsync(id, duplicateRemoval, "reviewer")).Error);
        Assert.Equal(result.Details.RowVersion, (await service.GetProfileAsync(id)).Details!.RowVersion);
    }

    [Theory]
    [InlineData("duplicate-entry")]
    [InlineData("foreign-entry")]
    [InlineData("foreign-removal")]
    [InlineData("duplicate-removal")]
    [InlineData("retained-and-removed")]
    public async Task Review_InvalidEntryCommandsLeaveAllClinicalDataUnchanged(string kind)
    {
        await using var db = database.CreateContext();
        var service = Service(db);
        var a = (await service.CreateAsync(Patient(Guid.NewGuid().ToString("N")))).Details!.PatientId;
        var b = (await service.CreateAsync(Patient(Guid.NewGuid().ToString("N")))).Details!.PatientId;
        var original = (await service.SaveProfileAsync(a, PatientRecordsTests.Snapshot(), "doctor")).Details!;
        var other = (await service.SaveProfileAsync(b, PatientRecordsTests.Snapshot(), "doctor")).Details!;
        var input = PatientRecordsTests.Retain(original);
        input = kind switch {
            "duplicate-entry" => input with { Allergies = [input.Allergies[0], input.Allergies[0]] },
            "foreign-entry" => input with { Allergies = [input.Allergies[0] with { Id = other.Allergies[0].Id }] },
            "foreign-removal" => input with { SupersededEntryIds = [other.Allergies[0].Id] },
            "duplicate-removal" => input with { SupersededEntryIds = [original.Allergies[0].Id, original.Allergies[0].Id] },
            _ => input with { SupersededEntryIds = [original.Allergies[0].Id] }
        };
        Assert.Equal(MedicalProfileError.InvalidEntryReference, (await service.SaveProfileAsync(a, input, "doctor")).Error);
        await db.SaveChangesAsync();
        Assert.Equal(original.RowVersion, (await service.GetProfileAsync(a)).Details!.RowVersion);
        Assert.Equal(other.RowVersion, (await service.GetProfileAsync(b)).Details!.RowVersion);
    }
    [Fact]
    public async Task Review_AdminSqlConflictPreservesWinnerAndReturnedVersion()
    {
        Guid id; byte[] version;
        await using (var setup = database.CreateContext())
        {
            var created = (await Service(setup).CreateAsync(Patient(Guid.NewGuid().ToString("N")))).Details!;
            id = created.PatientId; version = created.RowVersion;
        }
        await using var a = database.CreateContext();
        await using var b = database.CreateContext();
        await new PatientRecordsStore(a).PatientAsync(id, default);
        await new PatientRecordsStore(b).PatientAsync(id, default);
        var request = new UpdatePatientRequest("Synthetic winner", "shared", null, null, null, null, version);
        var winner = await Service(a).UpdateAsync(id, request);
        Assert.True(winner.IsSuccess);
        Assert.Equal(PatientAdminError.PatientChanged, (await Service(b).UpdateAsync(id, request with { FullName = "Synthetic loser" })).Error);
        await b.SaveChangesAsync();
        await using var verify = database.CreateContext();
        var saved = await verify.Patients.SingleAsync(x => x.Id == id);
        Assert.Equal("Synthetic winner", saved.FullName);
        Assert.Equal(winner.Details!.RowVersion, saved.RowVersion);
        Assert.NotEqual(version, saved.RowVersion);
    }
    private sealed class FailingSave : SaveChangesInterceptor
    {
        public bool Enabled = true;
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data, InterceptionResult<int> result, CancellationToken ct = default)
        {
            if (Enabled) throw new InvalidOperationException("Synthetic save failure.");
            return ValueTask.FromResult(result);
        }
    }

    private sealed class SaveGate : SaveChangesInterceptor
    {
        private int arrivals;
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data, InterceptionResult<int> result, CancellationToken ct = default)
        {
            if (Interlocked.Increment(ref arrivals) == 2) release.TrySetResult();
            await release.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);
            return result;
        }
    }
}
