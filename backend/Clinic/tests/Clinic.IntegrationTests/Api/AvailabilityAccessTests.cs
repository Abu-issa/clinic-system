using System.Net;
using Clinic.Application.Appointments;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace Clinic.IntegrationTests.Api;

public sealed class AvailabilityAccessTests(ErrorHandlingApiFactory factory) : IClassFixture<ErrorHandlingApiFactory>
{
    [Theory]
    [InlineData("anonymous", 401)]
    [InlineData("role", 403)]
    [InlineData("mfa", 403)]
    [InlineData("permission", 403)]
    [InlineData("scope", 403)]
    public async Task AvailabilityRequiresItsOwnPermissionAndDoctorScope(string scenario, int status)
    {
        var doctor = Guid.NewGuid();
        using var client = scenario == "anonymous"
            ? factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false })
            : AppointmentReschedulingHttpTests.CreateClient(factory, scenario == "scope" ? Guid.NewGuid() : doctor,
                role: scenario == "role" ? "Patient" : "Receptionist", mfa: scenario != "mfa",
                permission: scenario == "permission" ? "appointments.reschedule" : "appointments.availability");
        using var result = await client.GetAsync($"/api/staff/doctors/{doctor}/availability?date=2030-01-07&appointmentType=Consultation");
        Assert.Equal((HttpStatusCode)status, result.StatusCode);
        Assert.Null(result.Headers.Location);
    }

    [Theory]
    [InlineData("Doctor", "date=bad&appointmentType=Consultation", "invalid_date")]
    [InlineData("Receptionist", "appointmentType=Consultation", "invalid_date")]
    [InlineData("Doctor", "date=2030-01-07&appointmentType=Unknown", "invalid_appointment_type")]
    [InlineData("Receptionist", "date=2030-01-07&appointmentType=99", "invalid_appointment_type")]
    [InlineData("Doctor", "date=2030-01-07", "invalid_appointment_type")]
    public async Task InvalidQueryReturnsProblemDetails(string role, string query, string code)
    {
        var doctor = Guid.NewGuid();
        using var client = AppointmentReschedulingHttpTests.CreateClient(factory, doctor,
            role: role, permission: "appointments.availability");
        using var result = await client.GetAsync($"/api/staff/doctors/{doctor}/availability?{query}");
        await AppointmentReschedulingHttpTests.AssertProblemAsync(result, HttpStatusCode.BadRequest, code);
    }

    [Theory]
    [InlineData("ConsultationMinutes", "0")]
    [InlineData("SlotStartIntervalMinutes", "0")]
    [InlineData("MinimumAdvanceNoticeMinutes", "-1")]
    [InlineData("BookingHorizonDays", "-1")]
    public void InvalidPolicyPreventsStartup(string setting, string value)
    {
        using var invalid = factory.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?> { [$"BookingPolicy:{setting}"] = value })));
        var exception = Assert.Throws<OptionsValidationException>(() => invalid.CreateClient());
        Assert.Equal(typeof(BookingPolicySettings), exception.OptionsType);
    }
}
