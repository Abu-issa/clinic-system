using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Clinic.IntegrationTests.Api;

public sealed class BookingApiFactory : WebApplicationFactory<Program>
{
    private readonly string _connectionString;
    private readonly string _fileRoot;

    public BookingApiFactory(string connectionString)
    {
        _connectionString = connectionString;
        // Each host gets its own disposable private storage root; integration tests never share
        // stored bytes with the repository working tree.
        _fileRoot = Path.Combine(Path.GetTempPath(), $"ClinicTests_files_{Guid.NewGuid():N}");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Production");

        builder.UseSetting(
            "ConnectionStrings:ClinicDb",
            _connectionString);
        // Production-environment hosts must make the explicit QuestPDF license decision and use
        // a non-placeholder clinic identity; tests use documented development-style values.
        builder.UseSetting("PrintLicensing:PdfLicenseType", "Community");
        builder.UseSetting("ClinicDisplay:Name", "Integration Test Clinic");
        builder.UseSetting("ClinicDisplay:AddressLine", "Integration Test Address");
        builder.UseSetting("ClinicDisplay:Phone", "+962 0 000 0000");
        // Local production storage is the documented reviewed opt-in for test hosts.
        builder.UseSetting("FileStorage:Provider", "Local");
        builder.UseSetting("FileStorage:LocalRoot", _fileRoot);
        builder.UseSetting("FileStorage:MaxFileSizeBytes", "26214400");
        builder.UseSetting("FileStorage:AllowLocalInProduction", "true");

        builder.ConfigureTestServices(services =>
        {
            services.AddControllers()
                .AddApplicationPart(
                    typeof(AntiforgeryProbeController).Assembly);
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && Directory.Exists(_fileRoot))
            Directory.Delete(_fileRoot, recursive: true);
    }
}
