using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Clinic.IntegrationTests.Api;

public sealed class ErrorHandlingApiFactory :
    WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Production");

        builder.UseSetting(
            "ConnectionStrings:ClinicDb",
            "Server=.;Database=ClinicHttpTests_NotUsed;" +
            "Trusted_Connection=True;Encrypt=True;" +
            "TrustServerCertificate=True");

        builder.ConfigureTestServices(services =>
        {
            services.AddControllers()
                .AddApplicationPart(
                    typeof(ExceptionProbeController).Assembly);
        });
    }
}
