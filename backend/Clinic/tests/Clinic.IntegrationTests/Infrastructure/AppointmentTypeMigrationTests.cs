using Clinic.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Clinic.IntegrationTests.Infrastructure;

public sealed class AppointmentTypeMigrationTests : IClassFixture<SqlDatabaseFixture>
{
    private readonly SqlDatabaseFixture _database;
    public AppointmentTypeMigrationTests(SqlDatabaseFixture database) => _database = database;

    [Fact]
    public async Task UpgradePreservesLegacyTimesAndLeavesTypeUnclassified()
    {
        await using var context = _database.CreateContext();
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync("20260912114838_AddAppointmentRowVersion");
        var doctor = new Doctor("Legacy synthetic doctor");
        var patient = new Patient("Legacy synthetic patient", "0790000001");
        context.Add(doctor);
        await context.SaveChangesAsync();
        // Insert only columns present at this historical schema version.
        await context.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO Patients (Id, FullName, PhoneNumber, CreatedAtUtc) VALUES ({patient.Id}, {patient.FullName}, {patient.PhoneNumber}, {patient.CreatedAtUtc})");
        var id = Guid.NewGuid();
        var start = new DateTimeOffset(2025, 1, 1, 8, 0, 0, TimeSpan.Zero);
        var end = start.AddMinutes(47);
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO Appointments (Id, PatientId, DoctorId, StartsAtUtc, EndsAtUtc, Status, CreatedAtUtc)
            VALUES ({id}, {patient.Id}, {doctor.Id}, {start}, {end}, {1}, {start})
            """);
        await migrator.MigrateAsync();
        var saved = await context.Appointments.AsNoTracking().SingleAsync(x => x.Id == id);
        Assert.Null(saved.Type);
        Assert.Equal(start, saved.StartsAtUtc);
        Assert.Equal(end, saved.EndsAtUtc);
    }
}
