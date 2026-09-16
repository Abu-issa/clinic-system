using Clinic.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;

namespace Clinic.IntegrationTests.Api;

// Production-startup configuration gates: the shipped synthetic clinic identity and an
// unconfigured QuestPDF license choice must prevent host startup in Production, while valid
// explicit values start normally. Development/test may deliberately use synthetic values.
public sealed class ClinicDisplayConfigurationTests
{
    private const string PlaceholderName = "Synthetic Clinic (configure real display name before production)";

    private static WebApplicationFactory<Program> Factory(Action<IWebHostBuilder> configure) =>
        new BookingApiFactory("Server=.;Database=ClinicTests_ConfigProbe;Trusted_Connection=True;TrustServerCertificate=True")
            .WithWebHostBuilder(configure);

    [Theory]
    [InlineData("ClinicDisplay:Name", PlaceholderName)]
    [InlineData("ClinicDisplay:AddressLine", "Configure clinic address line before production")]
    [InlineData("ClinicDisplay:Phone", "Configure clinic phone before production")]
    public async Task ProductionStartupRejectsShippedSyntheticClinicPlaceholders(string key, string value)
    {
        using var factory = Factory(builder => builder.UseSetting(key, value));
        Assert.ThrowsAny<Exception>(() => _ = factory.Services);
        await Task.CompletedTask;
    }

    [Theory]
    [InlineData("ClinicDisplay:Name", "")]
    [InlineData("ClinicDisplay:AddressLine", "  ")]
    [InlineData("ClinicDisplay:Phone", "")]
    public async Task ProductionStartupRejectsBlankRequiredClinicValues(string key, string value)
    {
        using var factory = Factory(builder => builder.UseSetting(key, value));
        Assert.ThrowsAny<Exception>(() => _ = factory.Services);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task ProductionStartupRejectsOversizedClinicValues()
    {
        using var factory = Factory(builder =>
            builder.UseSetting("ClinicDisplay:Name", new string('X', 201)));
        Assert.ThrowsAny<Exception>(() => _ = factory.Services);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task ProductionStartupRequiresAnExplicitSupportedQuestPdfLicenseChoice()
    {
        using var missing = Factory(builder =>
            builder.UseSetting("PrintLicensing:PdfLicenseType", ""));
        Assert.ThrowsAny<Exception>(() => _ = missing.Services);
        await Task.CompletedTask;

        using var unsupported = Factory(builder =>
            builder.UseSetting("PrintLicensing:PdfLicenseType", "Evaluation"));
        Assert.ThrowsAny<Exception>(() => _ = unsupported.Services);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task ProductionStartsWithExplicitValidConfiguration()
    {
        using var factory = Factory(builder =>
        {
            builder.UseSetting("ClinicDisplay:Name", "Real Configured Clinic");
            builder.UseSetting("ClinicDisplay:AddressLine", "1 Configured Street");
            builder.UseSetting("ClinicDisplay:Phone", "+962 6 555 5555");
            builder.UseSetting("PrintLicensing:PdfLicenseType", "Community");
        });
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false
        });
        using var response = await client.GetAsync("/api/staff/auth/csrf");
        Assert.True(response.IsSuccessStatusCode);
    }
}
