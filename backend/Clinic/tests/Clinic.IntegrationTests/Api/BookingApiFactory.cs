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

        builder.ConfigureTestServices(services =>
        {
            services.AddControllers()
                .AddApplicationPart(
                    typeof(AntiforgeryProbeController).Assembly);
        });
    }
}
