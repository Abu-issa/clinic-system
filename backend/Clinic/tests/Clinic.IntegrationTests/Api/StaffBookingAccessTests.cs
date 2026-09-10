using System.Net;
using System.Net.Http.Json;
using Clinic.Application.Appointments;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Clinic.IntegrationTests.Api;

public sealed class StaffBookingAccessTests :
    IClassFixture<ErrorHandlingApiFactory>
{
    private readonly ErrorHandlingApiFactory _factory;

    public StaffBookingAccessTests(ErrorHandlingApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Book_WithoutAuthentication_ReturnsUnauthorized()
    {
        using var client = _factory.CreateClient(
            new WebApplicationFactoryClientOptions
            {
                BaseAddress = new Uri("https://localhost"),
                AllowAutoRedirect = false
            });

        var start = DateTimeOffset.UtcNow.AddDays(7);

        var request = new BookAppointmentRequest(
            Guid.NewGuid(),
            Guid.NewGuid(),
            start,
            start.AddMinutes(30));

        using var response = await client.PostAsJsonAsync(
            "/api/staff/appointments",
            request);

        Assert.Equal(
            HttpStatusCode.Unauthorized,
            response.StatusCode);

        Assert.Null(response.Headers.Location);
    }
    public static HttpClient CreateAuthenticatedClient(
     WebApplicationFactory<Program> factory,
     string role,
     bool hasMfa)
    {
        var client = factory.CreateClient(
            new WebApplicationFactoryClientOptions
            {
                BaseAddress = new Uri("https://localhost"),
                AllowAutoRedirect = false
            });

        var claims = new List<Claim>
    {
        new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
        new(ClaimTypes.Name, "Integration test user"),
        new(ClaimTypes.Role, role)
    };

        if (hasMfa)
        {
            claims.Add(new Claim("amr", "mfa"));
        }

        var identity = new ClaimsIdentity(claims, "ClinicStaff");
        var principal = new ClaimsPrincipal(identity);

        var properties = new AuthenticationProperties
        {
            IssuedUtc = DateTimeOffset.UtcNow,
            ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(5)
        };

        var ticket = new AuthenticationTicket(
            principal,
            properties,
            "ClinicStaff");

        var options = factory.Services
            .GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get("ClinicStaff");

        var protectedTicket = options.TicketDataFormat.Protect(ticket);

        client.DefaultRequestHeaders.Add(
            "Cookie",
            $"{options.Cookie.Name}={protectedTicket}");

        client.DefaultRequestHeaders.Accept.ParseAdd(
            "application/problem+json");

        return client;
    }
    private static BookAppointmentRequest CreateRequest()
    {
        var start = DateTimeOffset.UtcNow.AddDays(7);

        return new BookAppointmentRequest(
            Guid.NewGuid(),
            Guid.NewGuid(),
            start,
            start.AddMinutes(30));
    }
    [Theory]
    [InlineData("Patient", true)]
    [InlineData("DoctorAssistant", true)]
    [InlineData("Doctor", false)]
    [InlineData("Receptionist", false)]
    public async Task Book_WithoutRequiredRoleOrMfa_ReturnsForbidden(
    string role,
    bool hasMfa)
    {
        using var client = CreateAuthenticatedClient(_factory, role, hasMfa);

        using var response = await client.PostAsJsonAsync(
            "/api/staff/appointments",
            CreateRequest());

        Assert.Equal(
            HttpStatusCode.Forbidden,
            response.StatusCode);

        Assert.Null(response.Headers.Location);
    }
    [Theory]
    [InlineData("Doctor")]
    [InlineData("Receptionist")]
    public async Task Book_WithPermissionButWithoutCsrf_ReturnsBadRequest(
    string role)
    {
        using var client = CreateAuthenticatedClient(
            _factory,
            role,
            hasMfa: true);

        using var response = await client.PostAsJsonAsync(
            "/api/staff/appointments",
            CreateRequest());

        Assert.Equal(
            HttpStatusCode.BadRequest,
            response.StatusCode);

        Assert.Equal(
            "application/problem+json",
            response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadAsStringAsync();

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        Assert.Equal(
            "invalid_csrf_token",
            root.GetProperty("code").GetString());

        Assert.False(string.IsNullOrWhiteSpace(
            root.GetProperty("traceId").GetString()));
    }
}
