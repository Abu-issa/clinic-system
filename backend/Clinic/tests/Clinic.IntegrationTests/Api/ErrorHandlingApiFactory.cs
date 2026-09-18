using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Clinic.IntegrationTests.Api;

public sealed class ErrorHandlingApiFactory :
    WebApplicationFactory<Program>
{
    private readonly Clinic.IntegrationTests.Infrastructure.SqlDatabaseFixture _database = new();
    private readonly string _fileRoot = Path.Combine(Path.GetTempPath(), $"ClinicTests_files_{Guid.NewGuid():N}");

    public ErrorHandlingApiFactory() => Task.Run(() => _database.InitializeAsync()).GetAwaiter().GetResult();

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing) return;
        Task.Run(() => _database.DisposeAsync()).GetAwaiter().GetResult();
        if (Directory.Exists(_fileRoot)) Directory.Delete(_fileRoot, recursive: true);
    }
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Production");

        builder.UseSetting(
            "ConnectionStrings:ClinicDb",
            _database.ConnectionString);
        builder.UseSetting("PrintLicensing:PdfLicenseType", "Community");
        builder.UseSetting("ClinicDisplay:Name", "Integration Test Clinic");
        builder.UseSetting("ClinicDisplay:AddressLine", "Integration Test Address");
        builder.UseSetting("ClinicDisplay:Phone", "+962 0 000 0000");
        builder.UseSetting("FileStorage:Provider", "Local");
        builder.UseSetting("FileStorage:LocalRoot", _fileRoot);
        builder.UseSetting("FileStorage:MaxFileSizeBytes", "26214400");
        builder.UseSetting("FileStorage:AllowLocalInProduction", "true");

        builder.ConfigureTestServices(services =>
        {
            services.AddControllers()
                .AddApplicationPart(
                    typeof(ExceptionProbeController).Assembly);
        });
    }
}
