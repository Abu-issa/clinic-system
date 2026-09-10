using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Clinic.Api.Appointments;
using Clinic.Application.Appointments;
using Clinic.Domain.Entities;
using Clinic.Domain.Enums;
using Clinic.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Clinic.IntegrationTests.Api;

public sealed class StaffBookingHttpTests :
    IClassFixture<SqlDatabaseFixture>
{
    private readonly SqlDatabaseFixture _database;

    public StaffBookingHttpTests(SqlDatabaseFixture database)
    {
        _database = database;
    }

    [Fact]
    public async Task Book_WithPermissionAndCsrf_ReturnsCreatedAndPersists()
    {
        var doctor = new Doctor("طبيب اختبار HTTP");
        var patient = new Patient("مريض اختبار HTTP", "0790000005");

        await using (var seedContext = _database.CreateContext())
        {
            seedContext.Doctors.Add(doctor);
            seedContext.Patients.Add(patient);
            seedContext.DoctorWorkingPeriods.AddRange(
    TestWorkingHours.CreatePeriods(doctor.Id));

            await seedContext.SaveChangesAsync();
        }

        using var factory =
            new BookingApiFactory(_database.ConnectionString);

        using var client =
            StaffBookingAccessTests.CreateAuthenticatedClient(
                factory,
                "Receptionist",
                hasMfa: true);

        // Obtain a real antiforgery token for the authenticated user.
        using var tokenResponse =
            await client.GetAsync("/__tests/antiforgery");

        Assert.Equal(HttpStatusCode.OK, tokenResponse.StatusCode);

        var tokenBody =
            await tokenResponse.Content.ReadAsStringAsync();

        using var tokenDocument = JsonDocument.Parse(tokenBody);

        var requestToken = tokenDocument.RootElement
            .GetProperty("requestToken")
            .GetString();

        Assert.False(string.IsNullOrWhiteSpace(requestToken));

        client.DefaultRequestHeaders.Add(
            "X-CSRF-TOKEN",
            requestToken!);

        var start = TestWorkingHours.CreateFutureStart();

        var request = new BookAppointmentRequest(
            patient.Id,
            doctor.Id,
            start,
            start.AddMinutes(30));

        using var response = await client.PostAsJsonAsync(
            "/api/staff/appointments",
            request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        Assert.Equal(
            "application/json",
            response.Content.Headers.ContentType?.MediaType);

        var result = await response.Content
            .ReadFromJsonAsync<BookAppointmentResponse>();

        Assert.NotNull(result);
        Assert.NotEqual(Guid.Empty, result.AppointmentId);

        await using var verification = _database.CreateContext();

        var appointments = await verification.Appointments
            .AsNoTracking()
            .Where(appointment => appointment.DoctorId == doctor.Id)
            .ToListAsync();

        var saved = Assert.Single(appointments);

        Assert.Equal(result.AppointmentId, saved.Id);
        Assert.Equal(patient.Id, saved.PatientId);
        Assert.Equal(doctor.Id, saved.DoctorId);
        Assert.Equal(request.StartsAt, saved.StartsAtUtc);
        Assert.Equal(request.EndsAt, saved.EndsAtUtc);
        Assert.Equal(AppointmentStatus.Pending, saved.Status);
    }
}
