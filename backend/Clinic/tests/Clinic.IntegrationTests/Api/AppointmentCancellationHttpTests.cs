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

public sealed class AppointmentCancellationHttpTests :
    IClassFixture<SqlDatabaseFixture>
{
    private readonly SqlDatabaseFixture _database;

    public AppointmentCancellationHttpTests(
        SqlDatabaseFixture database)
    {
        _database = database;
    }

    [Fact]
    public async Task Cancel_WithPermissionAndCsrf_UsesAuthenticatedActor()
    {
        var doctor = new Doctor("طبيب اختبار إلغاء HTTP");
        var patient = new Patient(
            "مريض تجريبي",
            "+962790000006");

        var startsAtUtc = TestWorkingHours.CreateFutureStart();
        var endsAtUtc = startsAtUtc.AddMinutes(30);

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
            seed.Appointments.Add(appointment);

            await seed.SaveChangesAsync();
        }

        const string actorUserId = "authenticated-receptionist";

        using var factory = new BookingApiFactory(
            _database.ConnectionString);

        using var client = CreateAuthorizedClient(
            factory,
            doctor.Id,
            actorUserId);

        using (var csrfResponse = await client.GetAsync(
            "/__tests/antiforgery"))
        {
            Assert.Equal(HttpStatusCode.OK, csrfResponse.StatusCode);

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

        var url =
            $"/api/staff/doctors/{doctor.Id}/appointments/" +
            $"{appointment.Id}/cancellation";

        var beforeCancellation = DateTimeOffset.UtcNow;

        using var response = await client.PostAsJsonAsync(
            url,
            new
            {
                reason = "  طلب المريض الإلغاء  ",

                // حقل زائد لا يجوز أن يحدد المنفّذ الفعلي.
                actorUserId = "forged-user"
            });

        var afterCancellation = DateTimeOffset.UtcNow;

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsStringAsync());

        await using var verification = _database.CreateContext();

        var saved = await verification.Appointments
            .SingleAsync(x => x.Id == appointment.Id);

        Assert.Equal(AppointmentStatus.Cancelled, saved.Status);
        Assert.Equal("طلب المريض الإلغاء", saved.CancellationReason);
        Assert.Equal(actorUserId, saved.CancelledByUserId);
        Assert.True(saved.CancelledAtUtc.HasValue);

        Assert.InRange(
            saved.CancelledAtUtc.Value,
            beforeCancellation,
            afterCancellation);

        Assert.Equal(
            TimeSpan.Zero,
            saved.CancelledAtUtc.Value.Offset);

        Assert.Equal(startsAtUtc, saved.StartsAtUtc);
        Assert.Equal(endsAtUtc, saved.EndsAtUtc);
        Assert.Equal(patient.Id, saved.PatientId);
        Assert.Equal(doctor.Id, saved.DoctorId);
    }
    [Theory]
    [InlineData("unauthorized_doctor", 403, "appointment_access_denied")]
    [InlineData("appointment_from_other_doctor", 404, "appointment_not_found")]
    [InlineData("missing_csrf", 400, "invalid_csrf_token")]
    public async Task Cancel_WhenRejected_DoesNotChangeAppointment(
    string scenario,
    int expectedStatusCode,
    string expectedCode)
    {
        var doctor = new Doctor("طبيب الموعد");
        var otherDoctor = new Doctor("طبيب آخر");

        var patient = new Patient(
            "مريض اختبار منع الإلغاء",
            "+962790000007");

        var startsAtUtc = TestWorkingHours.CreateFutureStart();
        var endsAtUtc = startsAtUtc.AddMinutes(30);

        var appointment = new Appointment(
            patient.Id,
            doctor.Id,
            startsAtUtc,
            endsAtUtc);

        appointment.Confirm();

        await using (var seed = _database.CreateContext())
        {
            seed.Doctors.AddRange(doctor, otherDoctor);
            seed.Patients.Add(patient);
            seed.Appointments.Add(appointment);

            await seed.SaveChangesAsync();
        }

        var allowedDoctorId = doctor.Id;
        var requestedDoctorId = doctor.Id;

        if (scenario == "unauthorized_doctor")
        {
            // المستخدم مصرّح لطبيب الموعد، لكنه يطلب طبيبًا آخر.
            requestedDoctorId = otherDoctor.Id;
        }
        else if (scenario == "appointment_from_other_doctor")
        {
            // المستخدم مصرّح للطبيب الآخر، لكنه يرسل رقم موعد
            // لا يتبع لهذا الطبيب.
            allowedDoctorId = otherDoctor.Id;
            requestedDoctorId = otherDoctor.Id;
        }

        using var factory = new BookingApiFactory(
            _database.ConnectionString);

        using var client = CreateAuthorizedClient(
            factory,
            allowedDoctorId,
            "test-receptionist");

        if (scenario != "missing_csrf")
        {
            using var csrfResponse = await client.GetAsync(
                "/__tests/antiforgery");

            Assert.Equal(HttpStatusCode.OK, csrfResponse.StatusCode);

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

        var url =
            $"/api/staff/doctors/{requestedDoctorId}/appointments/" +
            $"{appointment.Id}/cancellation";

        using var response = await client.PostAsJsonAsync(
            url,
            new
            {
                reason = "محاولة إلغاء تجريبية"
            });

        Assert.Equal(
            (HttpStatusCode)expectedStatusCode,
            response.StatusCode);

        Assert.Equal(
            "application/problem+json",
            response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadAsStringAsync();

        using var document = JsonDocument.Parse(body);

        Assert.Equal(
            expectedCode,
            document.RootElement.GetProperty("code").GetString());

        Assert.False(string.IsNullOrWhiteSpace(
            document.RootElement.GetProperty("traceId").GetString()));

        await using var verification = _database.CreateContext();

        var saved = await verification.Appointments
            .SingleAsync(x => x.Id == appointment.Id);

        Assert.Equal(AppointmentStatus.Confirmed, saved.Status);
        Assert.Equal(doctor.Id, saved.DoctorId);
        Assert.Equal(patient.Id, saved.PatientId);
        Assert.Equal(startsAtUtc, saved.StartsAtUtc);
        Assert.Equal(endsAtUtc, saved.EndsAtUtc);

        Assert.Null(saved.CancellationReason);
        Assert.Null(saved.CancelledByUserId);
        Assert.Null(saved.CancelledAtUtc);
    }
    private static HttpClient CreateAuthorizedClient(
        WebApplicationFactory<Program> factory,
        Guid doctorId,
        string actorUserId)
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
            new Claim(ClaimTypes.NameIdentifier, actorUserId),
            new Claim(ClaimTypes.Name, "Cancellation HTTP test"),
            new Claim(ClaimTypes.Role, "Receptionist"),
            new Claim("amr", "mfa"),
            new Claim("permission", "appointments.cancel"),
            new Claim("appointment_doctor_id", doctorId.ToString())
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
