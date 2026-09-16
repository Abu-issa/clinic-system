using Clinic.Application.Medications;
using Clinic.Application.Prescriptions;
using Clinic.Domain.Entities;
using Clinic.Domain.Enums;
using Clinic.Infrastructure.Audit;
using Clinic.Infrastructure.Persistence;
using Clinic.Infrastructure.Repositories;
using Clinic.IntegrationTests.Infrastructure;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Clinic.IntegrationTests.Prescriptions;

public sealed class PrescriptionPersistenceTests(SqlDatabaseFixture database) : IClassFixture<SqlDatabaseFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 9, 15, 10, 0, 0, TimeSpan.Zero);
    private sealed class FixedClock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }
    private static PrescriptionService Service(ClinicDbContext db) => new(new PrescriptionStore(db), new FixedClock(), new AuditEventStore(db, new FixedClock()));
    private static MedicationCatalogService Catalog(ClinicDbContext db) => new(new MedicationCatalogStore(db), new FixedClock(), new AuditEventStore(db, new FixedClock()));

    private static async Task<Patient> SeedPatientAsync(ClinicDbContext db)
    {
        var patient = new Patient("Synthetic prescription patient", "shared");
        db.Patients.Add(patient);
        await db.SaveChangesAsync();
        return patient;
    }

    private static async Task<Doctor> SeedDoctorAsync(ClinicDbContext db)
    {
        var doctor = new Doctor("Synthetic prescription doctor");
        db.Doctors.Add(doctor);
        await db.SaveChangesAsync();
        return doctor;
    }

    private static async Task<Medication> SeedMedicationAsync(ClinicDbContext db, string strength = "500", string unit = "mg")
    {
        // Catalog identity is unique per fixture database, so every seed gets its own generic name.
        var name = $"Ibuprofen-{Guid.NewGuid():N}";
        var medication = new Medication(name, null, null, null, strength, unit,
            DosageForm.Tablet, MedicationRoute.Oral, null, "admin", Now);
        db.Medications.Add(medication);
        await db.SaveChangesAsync();
        return medication;
    }

    private static async Task<(PrescriptionService Service, Guid PatientId, Guid DoctorId, Guid VisitId)> SeedVisitAsync(ClinicDbContext db)
    {
        var patient = await SeedPatientAsync(db);
        var doctor = await SeedDoctorAsync(db);
        var visit = new Visit(patient.Id, doctor.Id, null, Now, "synthetic-doctor", Now);
        db.Add(visit);
        await db.SaveChangesAsync();
        return (Service(db), patient.Id, doctor.Id, visit.Id);
    }

    private static async Task<PrescriptionDetails> FinalizedDraftAsync(ClinicDbContext db, Guid patientId, Guid visitId, Medication? medication = null)
    {
        var service = Service(db);
        medication ??= await SeedMedicationAsync(db);
        var draft = (await service.CreateDraftAsync(new(visitId), "synthetic-doctor")).Details!;
        draft = (await service.AddItemAsync(patientId, draft.Id,
            new(medication.Id, "1 tablet", "twice daily", "7 days", null, null, draft.RowVersion), "synthetic-doctor")).Details!;
        var finalized = (await service.FinalizeAsync(patientId, draft.Id, draft.RowVersion, "synthetic-doctor")).Details!;
        return finalized;
    }

    [Fact]
    public async Task FullLifecycleRoundTripsWithIncompleteDraftItems()
    {
        await using var db = database.CreateContext();
        var (service, patientId, _, visitId) = await SeedVisitAsync(db);
        var medication = await SeedMedicationAsync(db);

        var draft = (await service.CreateDraftAsync(new(visitId, "initial notes"), "synthetic-doctor")).Details!;
        Assert.Equal(PrescriptionStatus.Draft, draft.Status);
        Assert.Equal(8, draft.RowVersion.Length);

        // Concern 1: an item may be saved while structurally incomplete.
        var item = (await service.AddItemAsync(patientId, draft.Id,
            new(medication.Id, null, null, null, "still deciding", null, draft.RowVersion), "synthetic-doctor")).Details!;
        Assert.Null(item.Items.Single().Dose);
        Assert.Null(item.Items.Single().Frequency);
        Assert.Null(item.Items.Single().Duration);

        // Completing and finalizing enforces structural completeness.
        item = (await service.UpdateItemAsync(patientId, draft.Id, item.Items.Single().Id,
            new(null, "1 tablet", "twice daily", "7 days", null, null, item.RowVersion), "synthetic-doctor")).Details!;
        var finalized = (await service.FinalizeAsync(patientId, draft.Id, item.RowVersion, "finalizer")).Details!;
        Assert.Equal(PrescriptionStatus.Finalized, finalized.Status);
        var released = (await service.ReleaseAsync(patientId, draft.Id, finalized.RowVersion, "pharmacy")).Details!;
        Assert.Equal(PrescriptionStatus.Released, released.Status);
        var cancelled = (await service.CancelAsync(patientId, draft.Id,
            new("wrong strength dispensed", released.RowVersion), "doctor")).Details!;
        Assert.Equal(PrescriptionStatus.Cancelled, cancelled.Status);
        Assert.Equal("wrong strength dispensed", cancelled.CancellationReason);

        await using var verify = database.CreateContext();
        var saved = (await Service(verify).GetAsync(patientId, draft.Id)).Details!;
        Assert.Equal(PrescriptionStatus.Cancelled, saved.Status);
        Assert.Equal("initial notes", saved.Notes);
        Assert.Equal("finalizer", saved.FinalizedByStaffId);
        Assert.Equal(Now, saved.FinalizedAtUtc);
        Assert.Equal("pharmacy", saved.ReleasedByStaffId);
        Assert.Equal("doctor", saved.CancelledByStaffId);
        var savedItem = Assert.Single(saved.Items);
        Assert.Equal("1 tablet", savedItem.Dose);
        Assert.Equal("twice daily", savedItem.Frequency);
        Assert.Equal("7 days", savedItem.Duration);
        Assert.Equal("500", savedItem.Strength);
        Assert.Equal("mg", savedItem.Unit);
        Assert.Equal(medication.Id, savedItem.MedicationId);
    }

    [Fact]
    public async Task FinalizationRejectsIncompleteItemsAndKeepsTheDraft()
    {
        await using var db = database.CreateContext();
        var (service, patientId, _, visitId) = await SeedVisitAsync(db);
        var medication = await SeedMedicationAsync(db);
        var draft = (await service.CreateDraftAsync(new(visitId), "synthetic-doctor")).Details!;
        draft = (await service.AddItemAsync(patientId, draft.Id,
            new(medication.Id, "1 tablet", null, null, null, null, draft.RowVersion), "synthetic-doctor")).Details!;

        var result = await service.FinalizeAsync(patientId, draft.Id, draft.RowVersion, "doctor");
        Assert.False(result.IsSuccess);
        Assert.Equal(PrescriptionError.InvalidInput, result.Error);

        await using var verify = database.CreateContext();
        var saved = (await Service(verify).GetAsync(patientId, draft.Id)).Details!;
        Assert.Equal(PrescriptionStatus.Draft, saved.Status);
    }

    [Fact]
    public async Task FinalizationRejectsDeactivatedMedication()
    {
        await using var db = database.CreateContext();
        var (service, patientId, _, visitId) = await SeedVisitAsync(db);
        var medication = await SeedMedicationAsync(db);
        var draft = (await service.CreateDraftAsync(new(visitId), "synthetic-doctor")).Details!;
        draft = (await service.AddItemAsync(patientId, draft.Id,
            new(medication.Id, "1 tablet", "twice daily", "7 days", null, null, draft.RowVersion), "synthetic-doctor")).Details!;

        // Concern 4: a deactivation committed before the eligibility check must be observed.
        medication.Deactivate("admin", Now);
        await db.SaveChangesAsync();

        var result = await service.FinalizeAsync(patientId, draft.Id, draft.RowVersion, "doctor");
        Assert.Equal(PrescriptionError.InactiveMedication, result.Error);
        await using var verify = database.CreateContext();
        Assert.Equal(PrescriptionStatus.Draft, (await Service(verify).GetAsync(patientId, draft.Id)).Details!.Status);
    }

    [Fact]
    public async Task ConcurrentDeactivationWaitsForTheFinalizationWindowThenApplies()
    {
        await using var db = database.CreateContext();
        var (service, patientId, _, visitId) = await SeedVisitAsync(db);
        var medication = await SeedMedicationAsync(db);
        var draft = (await service.CreateDraftAsync(new(visitId), "synthetic-doctor")).Details!;
        draft = (await service.AddItemAsync(patientId, draft.Id,
            new(medication.Id, "1 tablet", "twice daily", "7 days", null, null, draft.RowVersion), "synthetic-doctor")).Details!;

        // Concern 4, lock half: the finalization transaction's update-locked catalog read blocks
        // a deactivation until the finalization commits, so neither order loses the activity read.
        var store = new PrescriptionStore(db);
        var scope = await store.BeginMedicationActivityScopeAsync(CancellationToken.None);
        var locked = await scope.ReadActiveStatusAsync([medication.Id], CancellationToken.None);
        Assert.True(locked[medication.Id]);

        var deactivate = Task.Run(async () =>
        {
            await using var other = database.CreateContext();
            var result = await Catalog(other).DeactivateAsync(medication.Id, medication.RowVersion.ToArray(), "admin");
            return result;
        });
        try
        {
            await Task.Delay(1500);
            Assert.False(deactivate.IsCompleted, "Deactivation must wait behind the finalization catalog locks.");

            // Same sequence FinalizeAsync runs inside its scope transaction.
            var prescription = (await store.GetAsync(patientId, draft.Id, CancellationToken.None))!;
            prescription.FinalizePrescription(
                locked.ToDictionary(x => x.Key, x => x.Value), "doctor", Now);
            store.ExpectVersion(prescription, draft.RowVersion);
            await store.SaveAsync(CancellationToken.None);
            await scope.CommitAsync(CancellationToken.None);
        }
        finally
        {
            await scope.DisposeAsync();
        }

        var deactivation = await deactivate;
        Assert.True(deactivation.IsSuccess, deactivation.Error.ToString());

        await using var verify = database.CreateContext();
        var savedMed = await verify.Medications.SingleAsync(x => x.Id == medication.Id);
        Assert.False(savedMed.IsActive);
        var saved = (await Service(verify).GetAsync(patientId, draft.Id)).Details!;
        Assert.Equal(PrescriptionStatus.Finalized, saved.Status);
    }

    [Fact]
    public async Task DraftItemRemovalDeletesTheRowAndAdvancesTheVersion()
    {
        await using var db = database.CreateContext();
        var (service, patientId, _, visitId) = await SeedVisitAsync(db);
        var medication = await SeedMedicationAsync(db);
        var draft = (await service.CreateDraftAsync(new(visitId), "synthetic-doctor")).Details!;
        draft = (await service.AddItemAsync(patientId, draft.Id,
            new(medication.Id, "1 tablet", "twice daily", "7 days", null, null, draft.RowVersion), "doctor")).Details!;
        draft = (await service.AddItemAsync(patientId, draft.Id,
            new(medication.Id, "2 tablets", "twice daily", "7 days", null, 1, draft.RowVersion), "doctor")).Details!;
        var keep = draft.Items.OrderBy(x => x.DisplayOrder).Last();

        draft = (await service.RemoveItemAsync(patientId, draft.Id, draft.Items.First().Id, draft.RowVersion, "doctor")).Details!;
        Assert.Single(draft.Items, keep);

        // The restrictive database foreign key is satisfied because the severed row is deleted
        // with the aggregate save; no orphan item remains.
        await using var verify = database.CreateContext();
        Assert.Equal(1, await verify.Set<PrescriptionItem>().CountAsync(x => x.PrescriptionId == draft.Id));
        Assert.Equal(keep.Id, (await verify.Set<PrescriptionItem>().SingleAsync(x => x.PrescriptionId == draft.Id)).Id);
    }

    [Fact]
    public async Task FinalizedPrescriptionBlocksDirectItemMutationPaths()
    {
        await using var db = database.CreateContext();
        var (service, patientId, _, visitId) = await SeedVisitAsync(db);
        var medication = await SeedMedicationAsync(db);
        var draft = (await service.CreateDraftAsync(new(visitId), "synthetic-doctor")).Details!;
        draft = (await service.AddItemAsync(patientId, draft.Id,
            new(medication.Id, "1 tablet", "twice daily", "7 days", null, null, draft.RowVersion), "doctor")).Details!;
        var finalized = (await service.FinalizeAsync(patientId, draft.Id, draft.RowVersion, "doctor")).Details!;
        var item = Assert.Single(finalized.Items);

        // Every item-mutation route goes through the root and its Draft guard.
        Assert.Equal(PrescriptionError.InvalidLifecycle,
            (await service.UpdateItemAsync(patientId, draft.Id, item.Id,
                new(null, "9 tablets", null, null, null, null, finalized.RowVersion), "doctor")).Error);
        Assert.Equal(PrescriptionError.InvalidLifecycle,
            (await service.RemoveItemAsync(patientId, draft.Id, item.Id, finalized.RowVersion, "doctor")).Error);
        Assert.Equal(PrescriptionError.InvalidLifecycle,
            (await service.AddItemAsync(patientId, draft.Id,
                new(medication.Id, "1 tablet", "twice daily", "7 days", null, null, finalized.RowVersion), "doctor")).Error);
        Assert.Equal(PrescriptionError.InvalidLifecycle,
            (await service.ReorderItemsAsync(patientId, draft.Id,
                new([item.Id], finalized.RowVersion), "doctor")).Error);

        await using var verify = database.CreateContext();
        var saved = (await Service(verify).GetAsync(patientId, draft.Id)).Details!;
        Assert.Equal("1 tablet", Assert.Single(saved.Items).Dose);
    }

    [Fact]
    public async Task StaleRowVersionMutationIsRejectedAndSavesNothing()
    {
        await using var db = database.CreateContext();
        var (service, patientId, _, visitId) = await SeedVisitAsync(db);
        var medication = await SeedMedicationAsync(db);
        var draft = (await service.CreateDraftAsync(new(visitId), "synthetic-doctor")).Details!;
        var staleVersion = draft.RowVersion.ToArray();
        draft = (await service.UpdateDraftNotesAsync(patientId, draft.Id,
            new("moved on", draft.RowVersion), "doctor")).Details!;

        Assert.Equal(PrescriptionError.PrescriptionChanged,
            (await service.UpdateDraftNotesAsync(patientId, draft.Id, new("lost", staleVersion), "doctor")).Error);
        Assert.Equal(PrescriptionError.InvalidRowVersion,
            (await service.UpdateDraftNotesAsync(patientId, draft.Id, new("x", []), "doctor")).Error);

        await using var verify = database.CreateContext();
        Assert.Equal("moved on", (await Service(verify).GetAsync(patientId, draft.Id)).Details!.Notes);
    }

    [Fact]
    public async Task ConcurrentReplacementAttemptsProduceExactlyOneReplacement()
    {
        await using var db = database.CreateContext();
        var (_, patientId, _, visitId) = await SeedVisitAsync(db);
        var original = await FinalizedDraftAsync(db, patientId, visitId);
        original = (await Service(db).CancelAsync(patientId, original.Id,
            new("dosage error", original.RowVersion), "doctor")).Details!;

        // Concern 2: both attempts hold the original's rowversion; only one may claim it.
        var winner = (PrescriptionResult?)null;
        var loser = (PrescriptionResult?)null;
        var barrier = new TaskCompletionSource();
        var first = Task.Run(async () =>
        {
            await barrier.Task;
            await using var ctx = database.CreateContext();
            return await Service(ctx).CreateDraftAsync(
                new(visitId, null, original.Id, original.RowVersion.ToArray()), "doctor-a", CancellationToken.None);
        });
        var second = Task.Run(async () =>
        {
            await barrier.Task;
            await using var ctx = database.CreateContext();
            return await Service(ctx).CreateDraftAsync(
                new(visitId, null, original.Id, original.RowVersion.ToArray()), "doctor-b", CancellationToken.None);
        });
        barrier.SetResult();
        var results = await Task.WhenAll(first, second);
        // Deterministically identify the winner regardless of task completion order.
        if (results[0].IsSuccess) { winner = results[0]; loser = results[1]; }
        else { winner = results[1]; loser = results[0]; }

        Assert.True(winner!.IsSuccess, winner.Error.ToString());
        Assert.False(loser!.IsSuccess);
        // The loser fails on whichever guard fires first: the original's rowversion update
        // (PrescriptionChanged) or the forward-link unique index (ReplacementMismatch).
        Assert.True(loser.Error is PrescriptionError.PrescriptionChanged or PrescriptionError.ReplacementMismatch,
            loser.Error.ToString());

        await using var verify = database.CreateContext();
        var replacements = await verify.Set<Prescription>()
            .Where(x => x.ReplacesPrescriptionId == original.Id).ToListAsync();
        var savedOriginal = await verify.Set<Prescription>().SingleAsync(x => x.Id == original.Id);
        Assert.Single(replacements);
        // Both stored directions agree, and the reverse link is derivable and consistent.
        Assert.Equal(winner.Details!.Id, replacements.Single().Id);
        Assert.Equal(original.Id, replacements.Single().ReplacesPrescriptionId);
        Assert.Equal(winner.Details.Id, savedOriginal.ReplacedByPrescriptionId);
        Assert.Equal(PrescriptionStatus.Cancelled, savedOriginal.Status);
        Assert.Equal(1, await verify.Set<Prescription>().CountAsync(x => x.ReplacedByPrescriptionId == winner.Details.Id));
    }

    [Fact]
    public async Task ReplacementRequiresTheOriginalRowVersionAndAMatchingCancelledOriginal()
    {
        await using var db = database.CreateContext();
        var (service, patientId, _, visitId) = await SeedVisitAsync(db);
        var original = await FinalizedDraftAsync(db, patientId, visitId);
        var stillFinalized = original.RowVersion.ToArray();
        original = (await service.CancelAsync(patientId, original.Id,
            new("dosage error", original.RowVersion), "doctor")).Details!;
        var otherVisit = new Visit(patientId, original.DoctorId, null, Now, "doctor", Now);
        db.Add(otherVisit);
        await db.SaveChangesAsync();

        Assert.Equal(PrescriptionError.InvalidRowVersion,
            (await service.CreateDraftAsync(new(visitId, null, original.Id, null), "doctor")).Error);
        Assert.Equal(PrescriptionError.PrescriptionChanged,
            (await service.CreateDraftAsync(new(visitId, null, original.Id, stillFinalized), "doctor")).Error);
        Assert.Equal(PrescriptionError.PrescriptionNotFound,
            (await service.CreateDraftAsync(new(visitId, null, Guid.NewGuid(), original.RowVersion.ToArray()), "doctor")).Error);
        Assert.Equal(PrescriptionError.ReplacementMismatch,
            (await service.CreateDraftAsync(new(otherVisit.Id, null, original.Id, original.RowVersion.ToArray()), "doctor")).Error);

        // A second replacement after a successful one is refused before any write.
        var first = (await service.CreateDraftAsync(new(visitId, null, original.Id, original.RowVersion.ToArray()), "doctor")).Details!;
        Assert.Equal(PrescriptionError.ReplacementMismatch,
            (await service.CreateDraftAsync(new(visitId, null, original.Id, first.RowVersion), "doctor")).Error);

        await using var verify = database.CreateContext();
        Assert.Equal(1, await verify.Set<Prescription>().CountAsync(x => x.ReplacesPrescriptionId == original.Id));
        Assert.Equal(first.Id, (await verify.Set<Prescription>().SingleAsync(x => x.Id == original.Id)).ReplacedByPrescriptionId);
    }

    [Fact]
    public async Task TwoReplacementsForOneOriginalAreRejectedByTheDatabaseEvenWithoutTheService()
    {
        await using var db = database.CreateContext();
        var (_, patientId, _, visitId) = await SeedVisitAsync(db);
        var original = await FinalizedDraftAsync(db, patientId, visitId);
        original = (await Service(db).CancelAsync(patientId, original.Id,
            new("dosage error", original.RowVersion), "doctor")).Details!;

        // Direct writers bypass the service's rowversion serialization entirely; only the
        // filtered unique index on the forward link (ReplacesPrescriptionId) can stop a second
        // replacement referencing the same original.
        var raw = "INSERT INTO Prescriptions (Id, VisitId, PatientId, DoctorId, Status, ReplacesPrescriptionId, " +
                  "CreatedAtUtc, CreatedByStaffId, LastModifiedAtUtc, LastModifiedByStaffId) " +
                  "VALUES (@id, @visit, @patient, @doctor, 0, @original, SYSUTCDATETIME(), 'x', SYSUTCDATETIME(), 'x')";
        var connection = (SqlConnection)db.Database.GetDbConnection();
        await connection.OpenAsync();
        try
        {
            async Task<SqlCommand> CreateInsert(Guid id)
            {
                var command = new SqlCommand(raw, connection);
                command.Parameters.AddWithValue("@id", id);
                command.Parameters.AddWithValue("@visit", visitId);
                command.Parameters.AddWithValue("@patient", patientId);
                command.Parameters.AddWithValue("@doctor", original.DoctorId);
                command.Parameters.AddWithValue("@original", original.Id);
                return command;
            }

            await (await CreateInsert(Guid.NewGuid())).ExecuteNonQueryAsync();
            var second = await CreateInsert(Guid.NewGuid());
            var violation = await Assert.ThrowsAnyAsync<SqlException>(second.ExecuteNonQueryAsync);
            Assert.True(violation.Number is 2601 or 2627, $"Expected unique violation, got {violation.Number}.");
        }
        finally
        {
            await connection.CloseAsync();
        }

        await using var verify = database.CreateContext();
        Assert.Equal(1, await verify.Set<Prescription>().CountAsync(x => x.ReplacesPrescriptionId == original.Id));
    }

    [Fact]
    public async Task ChildOnlyItemRemovalAdvancesThePersistedRootRowVersion()
    {
        await using var db = database.CreateContext();
        var (service, patientId, _, visitId) = await SeedVisitAsync(db);
        var medication = await SeedMedicationAsync(db);
        var draft = (await service.CreateDraftAsync(new(visitId), "doctor")).Details!;
        draft = (await service.AddItemAsync(patientId, draft.Id,
            new(medication.Id, "1 tablet", "twice daily", "7 days", null, 0, draft.RowVersion), "doctor")).Details!;
        draft = (await service.AddItemAsync(patientId, draft.Id,
            new(medication.Id, "2 tablets", "twice daily", "7 days", null, 1, draft.RowVersion), "doctor")).Details!;
        var versionBeforeChildChange = draft.RowVersion.ToArray();

        draft = (await service.RemoveItemAsync(patientId, draft.Id, draft.Items.Last().Id, draft.RowVersion, "doctor")).Details!;

        // The child-only mutation issued a real root UPDATE: the persisted rowversion advanced,
        // and the returned details carry the newly persisted version.
        Assert.NotEqual(versionBeforeChildChange, draft.RowVersion);
        await using var verify = database.CreateContext();
        var persisted = await verify.Set<Prescription>().AsNoTracking().SingleAsync(x => x.Id == draft.Id);
        Assert.NotEqual(versionBeforeChildChange, persisted.RowVersion);
        Assert.Equal(draft.RowVersion, persisted.RowVersion);
    }

    [Fact]
    public async Task PrescriptionsAreScopedToTheirVisitAndPatient()
    {
        await using var db = database.CreateContext();
        var (service, patientId, _, visitId) = await SeedVisitAsync(db);
        var medication = await SeedMedicationAsync(db);
        var draft = (await service.CreateDraftAsync(new(visitId), "doctor")).Details!;
        await service.AddItemAsync(patientId, draft.Id,
            new(medication.Id, "1 tablet", "twice daily", "7 days", null, null, draft.RowVersion), "doctor");

        Assert.Equal(PrescriptionError.VisitNotFound,
            (await service.CreateDraftAsync(new(Guid.NewGuid()), "doctor")).Error);
        Assert.Equal(PrescriptionError.PrescriptionNotFound,
            (await service.GetAsync(patientId, Guid.NewGuid())).Error);
        // Cross-patient route cannot observe the prescription.
        var otherPatient = await SeedPatientAsync(db);
        Assert.Equal(PrescriptionError.PrescriptionNotFound,
            (await service.GetAsync(otherPatient.Id, draft.Id)).Error);
        Assert.Empty((await service.ListByVisitAsync(otherPatient.Id, visitId)).Prescriptions);
        Assert.Single((await service.ListByVisitAsync(patientId, visitId)).Prescriptions);
        Assert.Single((await service.ListByPatientAsync(patientId)).Prescriptions);
        Assert.False((await service.ListByPatientAsync(patientId, take: 101)).IsSuccess);
    }

    [Fact]
    public async Task LifecycleAuditShapeIsEnforcedByTheDatabase()
    {
        await using var db = database.CreateContext();
        var (_, patientId, doctorId, visitId) = await SeedVisitAsync(db);

        // Cancelled without finalization audit violates CK_Prescriptions_Lifecycle.
        var raw = new SqlCommand(
            "INSERT INTO Prescriptions (Id, VisitId, PatientId, DoctorId, Status, CreatedAtUtc, CreatedByStaffId, LastModifiedAtUtc, LastModifiedByStaffId) " +
            "VALUES (@id, @visit, @patient, @doctor, 3, SYSUTCDATETIME(), 'x', SYSUTCDATETIME(), 'x')");
        raw.Connection = (SqlConnection)db.Database.GetDbConnection();
        await raw.Connection.OpenAsync();
        try
        {
            raw.Parameters.AddWithValue("@id", Guid.NewGuid());
            raw.Parameters.AddWithValue("@visit", visitId);
            raw.Parameters.AddWithValue("@patient", patientId);
            raw.Parameters.AddWithValue("@doctor", doctorId);
            await Assert.ThrowsAnyAsync<SqlException>(raw.ExecuteNonQueryAsync);
        }
        finally
        {
            await raw.Connection.CloseAsync();
        }
    }
}
