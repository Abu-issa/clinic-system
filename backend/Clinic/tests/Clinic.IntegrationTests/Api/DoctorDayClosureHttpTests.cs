using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Clinic.Domain.Entities;
using Clinic.Domain.Enums;
using Clinic.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Clinic.IntegrationTests.Api;

public sealed class DoctorDayClosureHttpTests :
    IClassFixture<SqlDatabaseFixture>
{
    private readonly SqlDatabaseFixture _database;

    public DoctorDayClosureHttpTests(SqlDatabaseFixture database)
    {
        _database = database;
    }

    [Fact]
    public async Task Close_WithPermissionAndCsrf_PersistsClosure()
    {
        var doctor = new Doctor("طبيب اختبار إغلاق HTTP");
        var patient = new Patient(
            "مريض تجريبي",
            "+962790000003");

        var startsAtUtc = TestWorkingHours.CreateFutureStart();
        var endsAtUtc = startsAtUtc.AddMinutes(30);

        var localStart = TimeZoneInfo.ConvertTime(
            startsAtUtc,
            TestWorkingHours.ClinicTimeZone);

        var localDate = DateOnly.FromDateTime(localStart.DateTime);

        var appointment = new Appointment(
            patient.Id,
            doctor.Id,
            startsAtUtc,
            endsAtUtc);

        appointment.Confirm();

        await using (var seed = _database.CreateContext())
        {
            seed.Doctors.Add(doctor);
            seed.Patients.Add(patient);

            seed.DoctorWorkingPeriods.AddRange(
                TestWorkingHours.CreatePeriods(doctor.Id));

            seed.Appointments.Add(appointment);

            await seed.SaveChangesAsync();
        }

        using var factory = new BookingApiFactory(
            _database.ConnectionString);

        using var client = CreateAuthorizedClient(factory, doctor.Id);

        using (var csrfResponse = await client.GetAsync(
            "/__tests/antiforgery"))
        {
            Assert.Equal(
                HttpStatusCode.OK,
                csrfResponse.StatusCode);

            var csrfBody = await csrfResponse.Content.ReadAsStringAsync();

            using var csrfDocument = JsonDocument.Parse(csrfBody);

            var requestToken = csrfDocument.RootElement
                .GetProperty("requestToken")
                .GetString();

            Assert.False(string.IsNullOrWhiteSpace(requestToken));

            client.DefaultRequestHeaders.Add(
                "X-CSRF-TOKEN",
                requestToken!);
        }

        using var response = await client.PostAsJsonAsync(
            $"/api/staff/doctors/{doctor.Id}/day-closures",
            new
            {
                localDate,
                reason = "إجازة تجريبية عبر HTTP"
            });

        Assert.Equal(
            HttpStatusCode.Created,
            response.StatusCode);

        Assert.Equal(
            "application/json",
            response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadAsStringAsync();

        using var document = JsonDocument.Parse(body);

        var closureId = document.RootElement
            .GetProperty("closureId")
            .GetGuid();

        Assert.NotEqual(Guid.Empty, closureId);

        var affectedIds = document.RootElement
            .GetProperty("affectedAppointmentIds")
            .EnumerateArray()
            .Select(item => item.GetGuid())
            .ToArray();

        Assert.Equal(
            appointment.Id,
            Assert.Single(affectedIds));

        await using var verification = _database.CreateContext();

        var savedClosure = await verification.DoctorDayClosures
            .SingleAsync(x => x.DoctorId == doctor.Id);

        Assert.Equal(closureId, savedClosure.Id);
        Assert.Equal(localDate, savedClosure.LocalDate);
        Assert.Equal(
            "إجازة تجريبية عبر HTTP",
            savedClosure.Reason);

        var savedAppointment = await verification.Appointments
            .SingleAsync(x => x.Id == appointment.Id);

        Assert.Equal(
            AppointmentStatus.Confirmed,
            savedAppointment.Status);

        Assert.Equal(startsAtUtc, savedAppointment.StartsAtUtc);
        Assert.Equal(endsAtUtc, savedAppointment.EndsAtUtc);
    }

    private static HttpClient CreateAuthorizedClient(
        WebApplicationFactory<Program> factory,
        Guid doctorId)
    {
        var client = factory.CreateClient(
            new WebApplicationFactoryClientOptions
            {
                BaseAddress = new Uri("https://localhost"),
                AllowAutoRedirect = false,
                HandleCookies = true
            });

        var claims = new[]
        {
            new Claim(
                ClaimTypes.NameIdentifier,
                Guid.NewGuid().ToString()),
            new Claim(ClaimTypes.Name, "Schedule HTTP test"),
            new Claim(ClaimTypes.Role, "Doctor"),
            new Claim("amr", "mfa"),
            new Claim("permission", "schedule.manage"),
            new Claim("schedule_doctor_id", doctorId.ToString())
        };

        var identity = new ClaimsIdentity(claims, "ClinicStaff");

        var ticket = new AuthenticationTicket(
            new ClaimsPrincipal(identity),
            new AuthenticationProperties
            {
                IssuedUtc = DateTimeOffset.UtcNow,
                ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(5)
            },
            "ClinicStaff");

        var options = factory.Services
            .GetRequiredService<
                IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get("ClinicStaff");

        var protectedTicket = options.TicketDataFormat.Protect(ticket);

        client.DefaultRequestHeaders.Add(
            "Cookie",
            $"{options.Cookie.Name}={protectedTicket}");

        return client;
    }
}
