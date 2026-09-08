using Clinic.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Clinic.IntegrationTests.Infrastructure;

public sealed class SqlDatabaseFixture : IAsyncLifetime
{
    private readonly string _databaseName =
        $"ClinicTests_{Guid.NewGuid():N}";

    private readonly string _connectionString;

    public SqlDatabaseFixture()
    {
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = ".",
            InitialCatalog = _databaseName,
            IntegratedSecurity = true,
            TrustServerCertificate = true,
            Pooling = false
        };

        _connectionString = builder.ConnectionString;
    }

    public ClinicDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<ClinicDbContext>()
            .UseSqlServer(_connectionString)
            .Options;

        return new ClinicDbContext(options);
    }

    public async Task InitializeAsync()
    {
        await using var context = CreateContext();

        await context.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        var builder =
            new SqlConnectionStringBuilder(_connectionString);

        if (builder.InitialCatalog != _databaseName ||
            !_databaseName.StartsWith(
                "ClinicTests_",
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Refusing to delete a database outside this test fixture.");
        }

        await using var context = CreateContext();

        await context.Database.EnsureDeletedAsync();
    }
}
