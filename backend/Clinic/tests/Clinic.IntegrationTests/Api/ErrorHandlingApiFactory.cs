using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Clinic.IntegrationTests.Api;

public sealed class ErrorHandlingApiFactory :
    WebApplicationFactory<Program>
{
    private readonly Clinic.IntegrationTests.Infrastructure.SqlDatabaseFixture _database = new();

    public ErrorHandlingApiFactory() => Task.Run(() => _database.InitializeAsync()).GetAwaiter().GetResult();

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) Task.Run(() => _database.DisposeAsync()).GetAwaiter().GetResult();
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

        builder.ConfigureTestServices(services =>
        {
            services.AddControllers()
                .AddApplicationPart(
                    typeof(ExceptionProbeController).Assembly);
        });
    }
}
