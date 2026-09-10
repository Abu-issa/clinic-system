using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Clinic.IntegrationTests.Api;

public sealed class DoctorDayClosureAccessTests :
    IClassFixture<ErrorHandlingApiFactory>
{
    private readonly ErrorHandlingApiFactory _factory;

    public DoctorDayClosureAccessTests(
        ErrorHandlingApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Close_WhenAnonymous_Returns401()
    {
        using var client = CreateClient();

        using var response = await SendAsync(
            client,
            Guid.NewGuid());

        Assert.Equal(
            HttpStatusCode.Unauthorized,
            response.StatusCode);

        Assert.Null(response.Headers.Location);
    }

    [Theory]
    [InlineData("Patient", true, true, true)]
    [InlineData("DoctorAssistant", true, true, true)]
    [InlineData("Receptionist", true, true, true)]
    [InlineData("Doctor", false, true, true)]
    [InlineData("Doctor", true, false, true)]
    [InlineData("Doctor", true, true, false)]
    public async Task Close_WhenNotAuthorized_Returns403(
        string role,
        bool hasMfa,
        bool hasPermission,
        bool hasMatchingDoctor)
    {
        var requestedDoctorId = Guid.NewGuid();

        var allowedDoctorId = hasMatchingDoctor
            ? requestedDoctorId
            : Guid.NewGuid();

        using var client = CreateAuthenticatedClient(
            role,
            hasMfa,
            hasPermission,
            allowedDoctorId);

        using var response = await SendAsync(
            client,
            requestedDoctorId);

        Assert.Equal(
            HttpStatusCode.Forbidden,
            response.StatusCode);

        Assert.Null(response.Headers.Location);
    }

    [Fact]
    public async Task Close_WhenAuthorizedWithoutCsrf_Returns400()
    {
        var doctorId = Guid.NewGuid();

        using var client = CreateAuthenticatedClient(
            "Doctor",
            hasMfa: true,
            hasPermission: true,
            allowedDoctorId: doctorId);

        using var response = await SendAsync(client, doctorId);

        Assert.Equal(
            HttpStatusCode.BadRequest,
            response.StatusCode);

        Assert.Equal(
            "application/problem+json",
            response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadAsStringAsync();

        using var document = JsonDocument.Parse(body);

        Assert.Equal(
            "invalid_csrf_token",
            document.RootElement.GetProperty("code").GetString());
    }

    private HttpClient CreateAuthenticatedClient(
        string role,
        bool hasMfa,
        bool hasPermission,
        Guid allowedDoctorId)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new(ClaimTypes.Name, "Schedule access test"),
            new(ClaimTypes.Role, role),
            new("schedule_doctor_id", allowedDoctorId.ToString())
        };

        if (hasMfa)
        {
            claims.Add(new Claim("amr", "mfa"));
        }

        if (hasPermission)
        {
            claims.Add(new Claim("permission", "schedule.manage"));
        }

        var identity = new ClaimsIdentity(
            claims,
            "ClinicStaff");

        var ticket = new AuthenticationTicket(
            new ClaimsPrincipal(identity),
            new AuthenticationProperties
            {
                IssuedUtc = DateTimeOffset.UtcNow,
                ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(5)
            },
            "ClinicStaff");

        var options = _factory.Services
            .GetRequiredService<
                IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get("ClinicStaff");

        var protectedTicket = options.TicketDataFormat.Protect(ticket);

        var client = CreateClient();

        client.DefaultRequestHeaders.Add(
            "Cookie",
            $"{options.Cookie.Name}={protectedTicket}");

        return client;
    }

    private HttpClient CreateClient()
    {
        var client = _factory.CreateClient(
            new WebApplicationFactoryClientOptions
            {
                BaseAddress = new Uri("https://localhost"),
                AllowAutoRedirect = false
            });

        client.DefaultRequestHeaders.Accept.ParseAdd(
            "application/problem+json");

        return client;
    }

    private static Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        Guid doctorId)
    {
        return client.PostAsJsonAsync(
            $"/api/staff/doctors/{doctorId}/day-closures",
            new
            {
                localDate = new DateOnly(2026, 10, 20),
                reason = "إغلاق تجريبي"
            });
    }
}
