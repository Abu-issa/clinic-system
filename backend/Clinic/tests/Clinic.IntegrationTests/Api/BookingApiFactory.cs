using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Clinic.IntegrationTests.Api;

public sealed class BookingApiFactory : WebApplicationFactory<Program>
{
    private readonly string _connectionString;

    public BookingApiFactory(string connectionString)
    {
        _connectionString = connectionString;
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

        builder.ConfigureTestServices(services =>
        {
            services.AddControllers()
                .AddApplicationPart(
                    typeof(AntiforgeryProbeController).Assembly);
        });
    }
}
