using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Clinic.Api.Appointments;
using Clinic.Application.Appointments;
using Clinic.Domain.Entities;
using Clinic.Domain.Enums;
using Clinic.Infrastructure.Persistence;
using Clinic.Infrastructure.Repositories;
using Clinic.IntegrationTests.Api;
using Clinic.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Clinic.Application.Abstractions;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Data.Common;

namespace Clinic.IntegrationTests.Appointments;

public sealed class AvailabilityPolicyTests(SqlDatabaseFixture database) : IClassFixture<SqlDatabaseFixture>
{
    private readonly PolicyTestClock _clock = new();
    private static readonly DateOnly Date = new(2030, 1, 7);
    private static readonly BookingPolicy Policy = TestWorkingHours.Policy;
    private static DateTimeOffset At(int hour, int minute = 0) => new(
        TimeZoneInfo.ConvertTimeToUtc(Date.ToDateTime(new TimeOnly(hour, minute)), TestWorkingHours.ClinicTimeZone));

    private AppointmentAvailabilityService Availability(ClinicDbContext context) => new(
        new DoctorRepository(context), new WorkingScheduleRepository(context, TestWorkingHours.ClinicTimeZone),
        new AppointmentRepository(context), Policy, _clock);

    private AppointmentBookingService Booking(ClinicDbContext context) => new(
        new PatientRepository(context), new DoctorRepository(context), new AppointmentRepository(context),
        context, new SqlBookingTransaction(context), new WorkingScheduleRepository(context, TestWorkingHours.ClinicTimeZone),
        _clock, Policy);

    private async Task<(Doctor Doctor, Patient Patient)> SeedAsync(bool periods = true)
    {
        var doctor = new Doctor("Synthetic availability doctor");
        var patient = new Patient("Private synthetic patient", "0791234567");
        await using var context = database.CreateContext();
        context.AddRange(doctor, patient);
        if (periods) context.DoctorWorkingPeriods.AddRange(
            new DoctorWorkingPeriod(doctor.Id, Date.DayOfWeek, new(9, 0), new(10, 0)),
            new DoctorWorkingPeriod(doctor.Id, Date.DayOfWeek, new(13, 0), new(14, 0)),
            new DoctorWorkingPeriod(doctor.Id, Date.DayOfWeek, new(9, 0), new(10, 0)));
        await context.SaveChangesAsync();
        return (doctor, patient);
    }

    private WebApplicationFactory<Program> Factory() => new BookingApiFactory(database.ConnectionString)
        .WithWebHostBuilder(builder => builder.ConfigureTestServices(services => services.AddSingleton<TimeProvider>(_clock)));

    [Theory]
    [InlineData(AppointmentType.Consultation, 30)]
    [InlineData(AppointmentType.FollowUp, 15)]
    public async Task AvailabilityUsesConfiguredDuration_AndLaterOccupiedSlotCannotBeBooked(AppointmentType type, int duration)
    {
        var (doctor, patient) = await SeedAsync();
        await using var context = database.CreateContext();
        var available = await Availability(context).GetAsync(doctor.Id, Date, type);
        Assert.Equal(BookingError.None, available.Error);
        Assert.NotNull(available.Details);
        Assert.Equal(Date, available.Details.LocalDate);
        Assert.Equal("Asia/Amman", available.Details.TimeZoneId);
        Assert.Equal(type, available.Details.AppointmentType);
        Assert.All(available.Details.Slots, slot => Assert.Equal(TimeSpan.FromMinutes(duration), slot.EndsAtUtc - slot.StartsAtUtc));
        var slot = available.Details.Slots[0];
        await using (var first = database.CreateContext())
        {
            var result = await Booking(first).BookAsync(new(patient.Id, doctor.Id, slot.StartsAtUtc, type));
            Assert.True(result.IsSuccess);
        }
        await using (var second = database.CreateContext())
        {
            var result = await Booking(second).BookAsync(new(patient.Id, doctor.Id, slot.StartsAtUtc, type));
            Assert.Equal(BookingError.TimeSlotUnavailable, result.Error);
        }
        await using var verification = database.CreateContext();
        var saved = Assert.Single(await verification.Appointments.Where(x => x.DoctorId == doctor.Id).ToListAsync());
        Assert.Equal(type, saved.Type);
        Assert.Equal(slot.EndsAtUtc, saved.EndsAtUtc);
    }

    [Theory]
    [InlineData(AppointmentStatus.Pending, false)]
    [InlineData(AppointmentStatus.Confirmed, false)]
    [InlineData(AppointmentStatus.Arrived, false)]
    [InlineData(AppointmentStatus.InProgress, false)]
    [InlineData(AppointmentStatus.Completed, false)]
    [InlineData(AppointmentStatus.NoShow, false)]
    [InlineData(AppointmentStatus.Cancelled, true)]
    public async Task BlockingStatesMatchBooking_AndAdjacentIntervalsRemainFree(AppointmentStatus status, bool free)
    {
        var (doctor, patient) = await SeedAsync();
        var appointment = new Appointment(patient.Id, doctor.Id, At(9, 15), At(9, 45));
        if (status is AppointmentStatus.Confirmed or AppointmentStatus.Arrived or AppointmentStatus.InProgress or AppointmentStatus.Completed)
            appointment.Confirm();
        if (status is AppointmentStatus.Arrived or AppointmentStatus.InProgress or AppointmentStatus.Completed) appointment.MarkAsArrived();
        if (status is AppointmentStatus.InProgress or AppointmentStatus.Completed) appointment.StartVisit(At(9, 15));
        if (status == AppointmentStatus.Completed) appointment.Complete(At(9, 45));
        if (status == AppointmentStatus.NoShow) appointment.MarkAsNoShow(At(9, 45));
        if (status == AppointmentStatus.Cancelled) appointment.Cancel("Private reason", "private-actor", _clock.GetUtcNow());
        await using var context = database.CreateContext();
        context.Add(appointment);
        await context.SaveChangesAsync();
        var result = await Availability(context).GetAsync(doctor.Id, Date, AppointmentType.FollowUp);
        var slots = result.Details!.Slots;
        Assert.Contains(slots, s => s.StartsAtUtc == At(9));
        Assert.Contains(slots, s => s.StartsAtUtc == At(9, 45));
        Assert.Equal(free, slots.Any(s => s.StartsAtUtc == At(9, 15)));
        var repository = new AppointmentRepository(context);
        Assert.Equal(!free, await repository.HasOverlapAsync(doctor.Id, At(9, 15), At(9, 30)));
        var longer = await Availability(context).GetAsync(doctor.Id, Date, AppointmentType.Consultation);
        Assert.Equal(free, longer.Details!.Slots.Any(s => s.StartsAtUtc == At(9)));
    }

    [Theory]
    [InlineData("closed", BookingError.None)]
    [InlineData("no_periods", BookingError.None)]
    [InlineData("inactive", BookingError.DoctorInactive)]
    [InlineData("missing", BookingError.DoctorNotFound)]
    [InlineData("invalid_type", BookingError.InvalidAppointmentType)]
    [InlineData("invalid_doctor", BookingError.InvalidDoctorId)]
    public async Task UnavailableDoctorsAndDays_AreHandled(string scenario, BookingError error)
    {
        var (doctor, _) = await SeedAsync(scenario != "no_periods");
        await using var context = database.CreateContext();
        if (scenario == "closed") context.Add(new DoctorDayClosure(doctor.Id, Date, "Closed"));
        if (scenario == "inactive")
        {
            var tracked = await context.Doctors.SingleAsync(x => x.Id == doctor.Id);
            tracked.Deactivate();
        }
        await context.SaveChangesAsync();
        var result = await Availability(context).GetAsync(
            scenario == "missing" ? Guid.NewGuid() : scenario == "invalid_doctor" ? Guid.Empty : doctor.Id,
            Date, scenario == "invalid_type" ? (AppointmentType)99 : AppointmentType.Consultation);
        Assert.Equal(error, result.Error);
        if (error == BookingError.None) Assert.Empty(result.Details!.Slots);
    }

    [Fact]
    public async Task StaffAvailability_HasScopeAndPrivacy_AndBookingIgnoresClientEndTime()
    {
        var (doctor, patient) = await SeedAsync();
        using var factory = Factory();
        using var client = AppointmentReschedulingHttpTests.CreateClient(factory, doctor.Id,
            permission: "appointments.availability");
        var url = $"/api/staff/doctors/{doctor.Id}/availability?date=2030-01-07&appointmentType=FollowUp";
        using var response = await client.GetAsync(url); // No CSRF required.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(new[] { "appointmentType", "durationMinutes", "localDate", "slots", "timeZoneId" },
            json.RootElement.EnumerateObject().Select(p => p.Name).OrderBy(x => x));
        Assert.Equal("FollowUp", json.RootElement.GetProperty("appointmentType").GetString());
        foreach (var slot in json.RootElement.GetProperty("slots").EnumerateArray())
            Assert.Equal(new[] { "endsAtUtc", "startsAtUtc" }, slot.EnumerateObject().Select(p => p.Name).OrderBy(x => x));
        using var denied = AppointmentReschedulingHttpTests.CreateClient(factory, Guid.NewGuid(), permission: "appointments.availability");
        using var deniedResponse = await denied.GetAsync(url);
        await AppointmentReschedulingHttpTests.AssertProblemAsync(deniedResponse, HttpStatusCode.Forbidden, "appointment_access_denied");
        await AddCsrfAsync(client);
        using var booked = await client.PostAsJsonAsync("/api/staff/appointments", new
        {
            patientId = patient.Id, doctorId = doctor.Id, startsAt = At(9),
            appointmentType = "FollowUp", endsAt = At(14)
        });
        Assert.Equal(HttpStatusCode.Created, booked.StatusCode);
        await using var context = database.CreateContext();
        var saved = Assert.Single(await context.Appointments.Where(x => x.DoctorId == doctor.Id).ToListAsync());
        Assert.Equal(At(9, 15), saved.EndsAtUtc);
        Assert.Equal(AppointmentType.FollowUp, saved.Type);
        using var after = await client.GetAsync(url);
        var body = await after.Content.ReadAsStringAsync();
        Assert.DoesNotContain(patient.Id.ToString(), body);
        Assert.DoesNotContain(saved.Id.ToString(), body);
        Assert.DoesNotContain(patient.FullName, body);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(AppointmentType.FollowUp)]
    public async Task ReschedulingAvailability_UsesStoredDurationAndScopedExclusion(AppointmentType? type)
    {
        var (doctor, patient) = await SeedAsync();
        var appointment = new Appointment(patient.Id, doctor.Id, At(9), At(9, 45), type);
        appointment.Confirm();
        await using (var seed = database.CreateContext())
        {
            seed.Add(appointment);
            await seed.SaveChangesAsync();
        }
        using var factory = Factory();
        using var client = AppointmentReschedulingHttpTests.CreateClient(factory, doctor.Id);
        var url = AppointmentReschedulingHttpTests.Url(doctor.Id, appointment.Id);
        using var available = await client.GetAsync(url + "/availability?date=2030-01-07");
        Assert.Equal(HttpStatusCode.OK, available.StatusCode);
        using var json = JsonDocument.Parse(await available.Content.ReadAsStringAsync());
        Assert.Equal(45, json.RootElement.GetProperty("durationMinutes").GetDouble());
        Assert.Contains(json.RootElement.GetProperty("slots").EnumerateArray(), s =>
            s.GetProperty("startsAtUtc").GetDateTimeOffset() == At(9, 15) && s.GetProperty("endsAtUtc").GetDateTimeOffset() == At(10));
        using var otherClient = AppointmentReschedulingHttpTests.CreateClient(factory, Guid.NewGuid());
        using var forbidden = await otherClient.GetAsync(url + "/availability?date=2030-01-07");
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        var otherDoctor = Guid.NewGuid();
        using var scopedOther = AppointmentReschedulingHttpTests.CreateClient(factory, otherDoctor);
        using var notFound = await scopedOther.GetAsync(
            AppointmentReschedulingHttpTests.Url(otherDoctor, appointment.Id) + "/availability?date=2030-01-07");
        await AppointmentReschedulingHttpTests.AssertProblemAsync(notFound, HttpStatusCode.NotFound, "appointment_not_found");
        await AddCsrfAsync(client);
        using var wrongDuration = await client.PostAsJsonAsync(url,
            new RescheduleAppointmentBody(At(9, 15), At(9, 30), "Must not shorten", appointment.RowVersion));
        await AppointmentReschedulingHttpTests.AssertProblemAsync(wrongDuration, HttpStatusCode.BadRequest, "duration_changed");
        using var moved = await client.PostAsJsonAsync(url,
            new RescheduleAppointmentBody(At(9, 15), At(10), "Move", appointment.RowVersion));
        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
        await using var context = database.CreateContext();
        var saved = await context.Appointments.SingleAsync(x => x.Id == appointment.Id);
        Assert.Equal(type, saved.Type);
        Assert.Equal(doctor.Id, saved.DoctorId);
        Assert.Equal(AppointmentStatus.Confirmed, saved.Status);
        Assert.Equal(TimeSpan.FromMinutes(45), saved.EndsAtUtc - saved.StartsAtUtc);
        Assert.False(appointment.RowVersion.SequenceEqual(saved.RowVersion));
        Assert.Single(await context.AppointmentReschedules.Where(x => x.AppointmentId == appointment.Id).ToListAsync());
    }

    [Fact]
    public async Task GenerationUsesFourQueriesRegardlessOfCandidateCount()
    {
        var (doctor, _) = await SeedAsync();
        var counter = new QueryCounter();
        await using var context = new ClinicDbContext(new DbContextOptionsBuilder<ClinicDbContext>()
            .UseSqlServer(database.ConnectionString).AddInterceptors(counter).Options);
        var result = await Availability(context).GetAsync(doctor.Id, Date, AppointmentType.FollowUp);
        Assert.Equal(8, result.Details!.Slots.Count);
        Assert.Equal(4, counter.Reads);
    }

    [Theory]
    [InlineData("off_grid", "off_grid")]
    [InlineData("notice", "insufficient_notice")]
    [InlineData("horizon", "outside_booking_window")]
    public async Task BookingAndReschedulingEnforceTheSamePolicy(string scenario, string code)
    {
        var (doctor, patient) = await SeedAsync();
        var appointment = new Appointment(patient.Id, doctor.Id, At(9), At(9, 30), AppointmentType.Consultation);
        await using (var seed = database.CreateContext()) { seed.Add(appointment); await seed.SaveChangesAsync(); }
        var start = scenario == "off_grid" ? At(13, 1) : scenario == "horizon" ? At(13).AddDays(31) : At(13);
        if (scenario == "notice") _clock.Now = At(12, 1);
        using var factory = Factory();
        using var client = AppointmentReschedulingHttpTests.CreateClient(factory, doctor.Id);
        await AddCsrfAsync(client);
        using var booking = await client.PostAsJsonAsync("/api/staff/appointments",
            new BookAppointmentRequest(patient.Id, doctor.Id, start, AppointmentType.Consultation));
        await AppointmentReschedulingHttpTests.AssertProblemAsync(booking, HttpStatusCode.BadRequest, code);
        using var reschedule = await client.PostAsJsonAsync(AppointmentReschedulingHttpTests.Url(doctor.Id, appointment.Id),
            new RescheduleAppointmentBody(start, start.AddMinutes(30), "Move", appointment.RowVersion));
        await AppointmentReschedulingHttpTests.AssertProblemAsync(reschedule, HttpStatusCode.BadRequest, code);
        await using var verify = database.CreateContext();
        var saved = Assert.Single(await verify.Appointments.Where(x => x.DoctorId == doctor.Id).ToListAsync());
        Assert.Equal(At(9), saved.StartsAtUtc);
        Assert.Equal(appointment.RowVersion, saved.RowVersion);
        Assert.False(await verify.AppointmentReschedules.AnyAsync(x => x.AppointmentId == appointment.Id));
    }

    [Fact]
    public async Task NoticeIsRecheckedAfterAcquiringDoctorLock()
    {
        var (doctor, patient) = await SeedAsync();
        await using var context = database.CreateContext();
        var transaction = new AdvancingTransaction(new SqlBookingTransaction(context), () => _clock.Now = At(8, 1));
        var booking = new AppointmentBookingService(new PatientRepository(context), new DoctorRepository(context),
            new AppointmentRepository(context), context, transaction,
            new WorkingScheduleRepository(context, TestWorkingHours.ClinicTimeZone), _clock, Policy);
        Assert.Equal(BookingError.InsufficientNotice,
            (await booking.BookAsync(new(patient.Id, doctor.Id, At(9), AppointmentType.Consultation))).Error);
        Assert.False(await context.Appointments.AnyAsync(x => x.DoctorId == doctor.Id));
    }

    private sealed class AdvancingTransaction(IBookingTransaction inner, Action advance) : IBookingTransaction
    {
        public Task<T> ExecuteAsync<T>(Guid doctorId, Func<CancellationToken, Task<T>> operation,
            CancellationToken cancellationToken = default) => inner.ExecuteAsync(doctorId, token =>
            {
                advance();
                return operation(token);
            }, cancellationToken);
    }

    private sealed class QueryCounter : DbCommandInterceptor
    {
        public int Reads { get; private set; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Reads++;
            return ValueTask.FromResult(result);
        }
    }

    private static async Task AddCsrfAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/__tests/antiforgery");
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", json.RootElement.GetProperty("requestToken").GetString());
    }
}
