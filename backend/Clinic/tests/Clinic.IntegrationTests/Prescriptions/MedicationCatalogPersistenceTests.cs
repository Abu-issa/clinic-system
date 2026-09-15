using Clinic.Application.Medications;
using Clinic.Domain.Entities;
using Clinic.Domain.Enums;
using Clinic.Infrastructure.Persistence;
using Clinic.Infrastructure.Repositories;
using Clinic.IntegrationTests.Infrastructure;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Clinic.IntegrationTests.Prescriptions;

public sealed class MedicationCatalogPersistenceTests(SqlDatabaseFixture database) : IClassFixture<SqlDatabaseFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 9, 15, 10, 0, 0, TimeSpan.Zero);
    private sealed class FixedClock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }
    private static MedicationCatalogService Service(ClinicDbContext db) => new(new MedicationCatalogStore(db), new FixedClock());

    private static CreateMedicationRequest CreateRequest(
        string? genericEn = "Ibuprofen", string strength = "500", string unit = "mg", string? genericAr = null) =>
        new(genericEn, genericAr, "Advil", null, strength, unit, DosageForm.Tablet, MedicationRoute.Oral, "Analgesic");

    private static UpdateMedicationRequest UpdateRequest(
        CreateMedicationRequest source, byte[] expectedRowVersion) =>
        new(source.GenericNameEn, source.GenericNameAr, source.BrandNameEn, source.BrandNameAr,
            source.Strength, source.Unit, source.Form, source.Route, source.Category, expectedRowVersion);

    [Fact]
    public async Task CreateUpdateDeactivateActivateRoundTripWithVersions()
    {
        await using var db = database.CreateContext();
        var service = Service(db);
        var name = $"RoundTrip-{Guid.NewGuid():N}";
        var created = (await service.CreateAsync(CreateRequest(genericEn: name), "admin")).Details!;
        Assert.True(created.IsActive);
        Assert.Equal("500", created.Strength);
        Assert.Equal("mg", created.Unit);
        Assert.Equal(8, created.RowVersion.Length);

        var updated = (await service.UpdateAsync(created.Id,
            UpdateRequest(CreateRequest(genericEn: name) with { Strength = "400", Category = null }, created.RowVersion), "admin")).Details!;
        Assert.Equal("400", updated.Strength);
        Assert.Equal(MedicationCatalogError.MedicationChanged,
            (await service.UpdateAsync(created.Id, UpdateRequest(CreateRequest(genericEn: name) with { Strength = "200" }, created.RowVersion), "admin")).Error);

        var deactivated = (await service.DeactivateAsync(created.Id, updated.RowVersion, "admin")).Details!;
        Assert.False(deactivated.IsActive);
        Assert.True((await service.ActivateAsync(created.Id, deactivated.RowVersion, "admin")).IsSuccess);

        await using var verify = database.CreateContext();
        var saved = await verify.Medications.SingleAsync(x => x.Id == created.Id);
        Assert.True(saved.IsActive);
        Assert.Equal("400", saved.Strength);
    }

    [Fact]
    public async Task CreateRejectsDuplicateIdentitiesActiveOrInactive()
    {
        await using var db = database.CreateContext();
        var service = Service(db);
        var name = $"DuplicateCheck-{Guid.NewGuid():N}";
        var created = (await service.CreateAsync(CreateRequest(genericEn: name), "admin")).Details!;

        Assert.Equal(MedicationCatalogError.DuplicateMedication,
            (await service.CreateAsync(CreateRequest(genericEn: name, strength: "500"), "admin")).Error);
        // A different strength is a different product identity.
        Assert.True((await service.CreateAsync(CreateRequest(genericEn: name, strength: "400"), "admin")).IsSuccess);
        // Brand names and category do not distinguish products.
        Assert.Equal(MedicationCatalogError.DuplicateMedication,
            (await service.CreateAsync(CreateRequest(genericEn: name) with { BrandNameEn = "Nurofen" }, "admin")).Error);
        // Compound strengths are distinct identities.
        Assert.True((await service.CreateAsync(CreateRequest(genericEn: "Amoxicillin", strength: "500/125"), "admin")).IsSuccess);

        // Deactivating withdraws the product but does not free its identity for re-creation.
        await service.DeactivateAsync(created.Id, created.RowVersion, "admin");
        Assert.Equal(MedicationCatalogError.DuplicateMedication,
            (await service.CreateAsync(CreateRequest(genericEn: name), "admin")).Error);
    }

    [Fact]
    public async Task ConcurrentDuplicateCreatesProduceExactlyOneCatalogEntry()
    {
        var barrier = new TaskCompletionSource();
        var first = Task.Run(async () =>
        {
            await barrier.Task;
            await using var ctx = database.CreateContext();
            return await Service(ctx).CreateAsync(CreateRequest(genericEn: "Paracetamol"), "admin-a");
        });
        var second = Task.Run(async () =>
        {
            await barrier.Task;
            await using var ctx = database.CreateContext();
            return await Service(ctx).CreateAsync(CreateRequest(genericEn: "Paracetamol"), "admin-b");
        });
        barrier.SetResult();
        var results = await Task.WhenAll(first, second);

        Assert.Equal(1, results.Count(x => x.IsSuccess));
        Assert.Contains(results, x => !x.IsSuccess && x.Error == MedicationCatalogError.DuplicateMedication);

        await using var verify = database.CreateContext();
        Assert.Equal(1, await verify.Medications.CountAsync(x => x.GenericNameEn == "Paracetamol" && x.Strength == "500"));
    }

    [Fact]
    public async Task SearchFiltersByTermAndActiveStatus()
    {
        await using var db = database.CreateContext();
        var service = Service(db);
        var a = (await service.CreateAsync(CreateRequest(genericEn: "SearchTest-One"), "admin")).Details!;
        await service.CreateAsync(CreateRequest(genericEn: "SearchTest-Two", strength: "400"), "admin");
        await service.DeactivateAsync(a.Id, a.RowVersion, "admin");

        var all = await service.SearchAsync(new MedicationSearchQuery("SearchTest-", ActiveOnly: false));
        Assert.True(all.IsSuccess);
        Assert.Equal(2, all.TotalCount);
        var active = await service.SearchAsync(new MedicationSearchQuery("SearchTest-", ActiveOnly: true));
        Assert.Single(active.Items, x => x.GenericNameEn == "SearchTest-Two");
        Assert.False((await service.SearchAsync(new MedicationSearchQuery(Take: 101))).IsSuccess);
        Assert.False((await service.SearchAsync(new MedicationSearchQuery(Skip: -1))).IsSuccess);
    }

    [Fact]
    public async Task EmbeddedUnitInStrengthIsRejectedByDomainAndDatabase()
    {
        // Domain-level: "500 mg" plus Unit="mg" would be ambiguous.
        await using var db = database.CreateContext();
        Assert.Equal(MedicationCatalogError.InvalidInput,
            (await Service(db).CreateAsync(CreateRequest(strength: "500 mg"), "admin")).Error);
        Assert.Equal(MedicationCatalogError.InvalidInput,
            (await Service(db).CreateAsync(CreateRequest(unit: "mg ml"), "admin")).Error);

        // Direct writes are backstopped by the SQL shape constraints.
        var connection = (SqlConnection)db.Database.GetDbConnection();
        await connection.OpenAsync();
        try
        {
            foreach (var strength in new[] { "500 mg", "500  mg" })
            {
                await using var command = new SqlCommand(
                    "INSERT INTO Medications (Id, GenericNameEn, Strength, Unit, Form, Route, IsActive, CreatedAtUtc, CreatedByStaffId, LastModifiedAtUtc, LastModifiedByStaffId) " +
                    "VALUES (@id, @name, @strength, 'mg', 0, 0, 1, SYSUTCDATETIME(), 'x', SYSUTCDATETIME(), 'x')", connection);
                command.Parameters.AddWithValue("@id", Guid.NewGuid());
                command.Parameters.AddWithValue("@name", $"Raw-{Guid.NewGuid():N}");
                command.Parameters.AddWithValue("@strength", strength);
                await Assert.ThrowsAnyAsync<SqlException>(command.ExecuteNonQueryAsync);
            }
        }
        finally
        {
            await connection.CloseAsync();
        }
    }
}
