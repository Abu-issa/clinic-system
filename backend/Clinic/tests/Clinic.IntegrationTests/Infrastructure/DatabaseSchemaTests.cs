using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Clinic.IntegrationTests.Infrastructure;

public sealed class DatabaseSchemaTests :
    IClassFixture<SqlDatabaseFixture>
{
    private readonly SqlDatabaseFixture _database;

    public DatabaseSchemaTests(SqlDatabaseFixture database)
    {
        _database = database;
    }

    [Fact]
    public async Task Migrations_CreateReadableApplicationTables()
    {
        await using var context = _database.CreateContext();

        var appliedMigrations =
            await context.Database.GetAppliedMigrationsAsync();

        Assert.Contains(
            appliedMigrations,
            migration => migration.EndsWith(
                "_InitialCreate",
                StringComparison.Ordinal));

        var patients = await context.Patients
            .AsNoTracking()
            .Take(1)
            .ToListAsync();

        var doctors = await context.Doctors
            .AsNoTracking()
            .Take(1)
            .ToListAsync();

        var appointments = await context.Appointments
            .AsNoTracking()
            .Take(1)
            .ToListAsync();

        Assert.Empty(patients);
        Assert.Empty(doctors);
        Assert.Empty(appointments);
    }
}
