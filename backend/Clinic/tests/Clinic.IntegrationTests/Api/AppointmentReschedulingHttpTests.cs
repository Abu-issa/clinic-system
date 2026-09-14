using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Clinic.Api.Appointments;
using Clinic.Application.Appointments;
using Clinic.Domain.Entities;
using Clinic.Domain.Enums;
using Clinic.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Clinic.IntegrationTests.Api;

public sealed class AppointmentReschedulingHttpTests(SqlDatabaseFixture database)
    : IClassFixture<SqlDatabaseFixture>
{
    [Theory]
    [InlineData("Doctor")]
    [InlineData("Receptionist")]
    public async Task Reschedule_ReturnsPersistedVersionAndHistory_RejectsStaleRetry(string role)
    {
        var appointment = await SeedAsync();
        using var factory = new BookingApiFactory(database.ConnectionString);
        using var client = CreateClient(factory, appointment.DoctorId, role: role);
        var url = Url(appointment.DoctorId, appointment.Id);
        using var read = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.True(read.Headers.CacheControl?.NoStore);
        var details = await read.Content.ReadFromJsonAsync<AppointmentReschedulingDetails>();
        Assert.NotNull(details);
        Assert.Equal(appointment.Id, details.AppointmentId);
        Assert.Equal(appointment.DoctorId, details.DoctorId);
        Assert.Equal(appointment.RowVersion, details.RowVersion);
        Assert.Equal(appointment.StartsAtUtc, details.StartsAtUtc);
        Assert.Equal(appointment.EndsAtUtc, details.EndsAtUtc);
        Assert.Equal(AppointmentStatus.Confirmed, details.Status);
        await AddCsrfAsync(client);

        var start = appointment.StartsAtUtc.AddHours(1);
        using var response = await client.PostAsJsonAsync(url, new
        {
            startsAt = start,
            endsAt = start.AddMinutes(30),
            reason = "  Patient requested a move  ",
            expectedRowVersion = details.RowVersion,
            actorUserId = "forged-actor"
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<RescheduleAppointmentResponse>();
        Assert.NotNull(result);
        Assert.NotEqual(Guid.Empty, result.ChangeId);
        Assert.Equal(8, result.RowVersion.Length);
        Assert.False(details.RowVersion.SequenceEqual(result.RowVersion));

        using var stale = await client.PostAsJsonAsync(url, new RescheduleAppointmentBody(
            start.AddHours(1), start.AddHours(1).AddMinutes(30), "Stale retry", details.RowVersion));
        await AssertProblemAsync(stale, HttpStatusCode.Conflict, "appointment_changed");

        await using (var verification = database.CreateContext())
        {
            var saved = await verification.Appointments.SingleAsync(x => x.Id == appointment.Id);
            Assert.Equal(start, saved.StartsAtUtc);
            Assert.Equal(start.AddMinutes(30), saved.EndsAtUtc);
            Assert.Equal(AppointmentStatus.Confirmed, saved.Status);
            Assert.Equal(result.RowVersion, saved.RowVersion);
            var history = Assert.Single(await verification.AppointmentReschedules
                .Where(x => x.AppointmentId == appointment.Id).ToListAsync());
            Assert.Equal(result.ChangeId, history.Id);
            Assert.Equal("staff-user", history.ChangedByUserId);
            Assert.Equal("Patient requested a move", history.Reason);
        }

        var refreshed = await client.GetFromJsonAsync<AppointmentReschedulingDetails>(url);
        Assert.NotNull(refreshed);
        Assert.Equal(result.RowVersion, refreshed.RowVersion);
        // The returned version can be used for the next intentional edit.
        using var next = await client.PostAsJsonAsync(url, new RescheduleAppointmentBody(
            start.AddHours(1), start.AddHours(1).AddMinutes(30), "Next move", result.RowVersion));
        Assert.Equal(HttpStatusCode.OK, next.StatusCode);
    }

    [Theory]
    [InlineData("scope", 403, "appointment_access_denied")]
    [InlineData("other_appointment", 404, "appointment_not_found")]
    [InlineData("missing_csrf", 400, "invalid_csrf_token")]
    [InlineData("invalid_csrf", 400, "invalid_csrf_token")]
    [InlineData("invalid_version", 400, "invalid_row_version")]
    public async Task RejectedMutation_DoesNotChangeAppointmentOrHistory(
        string scenario, int status, string code)
    {
        var appointment = await SeedAsync();
        var requestedDoctor = scenario is "scope" or "other_appointment"
            ? Guid.NewGuid() : appointment.DoctorId;
        var allowedDoctor = scenario == "other_appointment" ? requestedDoctor : appointment.DoctorId;
        using var factory = new BookingApiFactory(database.ConnectionString);
        using var client = CreateClient(factory, allowedDoctor);
        if (scenario != "missing_csrf")
        {
            await AddCsrfAsync(client);
        }
        if (scenario == "invalid_csrf")
        {
            client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
            client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", "invalid");
        }

        using var response = await client.PostAsJsonAsync(Url(requestedDoctor, appointment.Id),
            new RescheduleAppointmentBody(appointment.StartsAtUtc.AddHours(1),
                appointment.EndsAtUtc.AddHours(1), "Move",
                scenario == "invalid_version" ? new byte[7] : appointment.RowVersion));
        await AssertProblemAsync(response, (HttpStatusCode)status, code);
        await using var verification = database.CreateContext();
        var saved = await verification.Appointments.SingleAsync(x => x.Id == appointment.Id);
        Assert.Equal(appointment.StartsAtUtc, saved.StartsAtUtc);
        Assert.Equal(appointment.EndsAtUtc, saved.EndsAtUtc);
        Assert.Equal(appointment.Status, saved.Status);
        Assert.Equal(appointment.RowVersion, saved.RowVersion);
        Assert.False(await verification.AppointmentReschedules.AnyAsync(x => x.AppointmentId == appointment.Id));
    }

    [Theory]
    [InlineData(false, 403)]
    [InlineData(true, 404)]
    public async Task Read_DoesNotExposeAnotherDoctorsAppointment(bool scopeMatchesRoute, int status)
    {
        var appointment = await SeedAsync();
        var requestedDoctor = Guid.NewGuid();
        using var factory = new BookingApiFactory(database.ConnectionString);
        using var client = CreateClient(factory, scopeMatchesRoute ? requestedDoctor : appointment.DoctorId);
        using var response = await client.GetAsync(Url(requestedDoctor, appointment.Id));
        await AssertProblemAsync(response, (HttpStatusCode)status,
            scopeMatchesRoute ? "appointment_not_found" : "appointment_access_denied");
    }

    private async Task<Appointment> SeedAsync()
    {
        var doctor = new Doctor("Rescheduling HTTP doctor");
        var patient = new Patient("Rescheduling HTTP patient", "0790000012");
        var start = TestWorkingHours.CreateFutureStart();
        var appointment = new Appointment(patient.Id, doctor.Id, start, start.AddMinutes(30));
        appointment.Confirm();
        await using var seed = database.CreateContext();
        seed.AddRange(doctor, patient, appointment);
        seed.DoctorWorkingPeriods.AddRange(TestWorkingHours.CreatePeriods(doctor.Id));
        await seed.SaveChangesAsync();
        return appointment;
    }

    internal static string Url(Guid doctorId, Guid appointmentId) =>
        $"/api/staff/doctors/{doctorId}/appointments/{appointmentId}/rescheduling";

    internal static HttpClient CreateClient(WebApplicationFactory<Program> factory,
        Guid doctorId, string role = "Receptionist", bool mfa = true,
        string permission = "appointments.reschedule", string? actor = "staff-user")
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false
        });
        var claims = new List<Claim>
        {
            new(ClaimTypes.Role, role),
            new("permission", permission),
            new("appointment_doctor_id", doctorId.ToString())
        };
        if (actor is not null) claims.Add(new(ClaimTypes.NameIdentifier, actor));
        if (mfa) claims.Add(new("amr", "mfa"));
        var ticket = new AuthenticationTicket(
            new ClaimsPrincipal(new ClaimsIdentity(claims, "ClinicStaff")),
            new AuthenticationProperties
            {
                IssuedUtc = factory.Services.GetRequiredService<TimeProvider>().GetUtcNow(),
                ExpiresUtc = factory.Services.GetRequiredService<TimeProvider>().GetUtcNow().AddMinutes(5)
            }, "ClinicStaff");
        var options = factory.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get("ClinicStaff");
        client.DefaultRequestHeaders.Add("Cookie", $"{options.Cookie.Name}={options.TicketDataFormat.Protect(ticket)}");
        return client;
    }

    private static async Task AddCsrfAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/__tests/antiforgery");
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", json.RootElement.GetProperty("requestToken").GetString());
    }

    internal static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(code, json.RootElement.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(json.RootElement.GetProperty("traceId").GetString()));
    }
}
