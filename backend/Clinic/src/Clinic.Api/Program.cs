using Clinic.Application.Abstractions;
using Clinic.Application.Appointments;
using Clinic.Infrastructure.Persistence;
using Clinic.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Clinic.Api.ErrorHandling;
using Clinic.Application.Schedules;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddOpenApi();
builder.Services.AddProblemDetails(options =>
{
    options.CustomizeProblemDetails = context =>
    {
        context.ProblemDetails.Extensions["traceId"] =
            context.HttpContext.TraceIdentifier;
    };
});

builder.Services.AddExceptionHandler<GlobalExceptionHandler>();

var connectionString =
    builder.Configuration.GetConnectionString("ClinicDb");

if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException(
        "Connection string 'ClinicDb' is missing.");
}

builder.Services.AddDbContext<ClinicDbContext>(options =>
{
    options.UseSqlServer(connectionString);
});

builder.Services.AddScoped<IPatientRepository, PatientRepository>();
builder.Services.AddScoped<IDoctorRepository, DoctorRepository>();
builder.Services.AddScoped<IAppointmentRepository, AppointmentRepository>();
builder.Services.AddScoped<DoctorDayClosureService>();
builder.Services.AddScoped<AppointmentCancellationService>();

builder.Services.AddScoped<IUnitOfWork>(provider =>
    provider.GetRequiredService<ClinicDbContext>());

builder.Services.AddScoped<AppointmentBookingService>();

builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
builder.Services.AddScoped<IBookingTransaction, SqlBookingTransaction>();
builder.Services
    .AddAuthentication("ClinicStaff")
    .AddCookie("ClinicStaff", options =>
    {
        options.Cookie.Name = "__Host-Clinic.Staff";
        options.Cookie.HttpOnly = true;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.Cookie.Path = "/";

        options.ExpireTimeSpan = TimeSpan.FromMinutes(30);
        options.SlidingExpiration = false;

        options.Events.OnRedirectToLogin = context =>
        {
            context.Response.StatusCode =
                StatusCodes.Status401Unauthorized;

            return Task.CompletedTask;
        };

        options.Events.OnRedirectToAccessDenied = context =>
        {
            context.Response.StatusCode =
                StatusCodes.Status403Forbidden;

            return Task.CompletedTask;
        };
    });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("StaffBooking", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireRole("Doctor", "Receptionist");
        policy.RequireClaim("amr", "mfa");
    });

    options.AddPolicy("StaffScheduleManagement", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireRole("Doctor");
        policy.RequireClaim("amr", "mfa");
        policy.RequireClaim("permission", "schedule.manage");
    });

    options.AddPolicy("ManageDoctorSchedule", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireRole("Doctor");
        policy.RequireClaim("amr", "mfa");
        policy.RequireClaim("permission", "schedule.manage");

        policy.RequireAssertion(context =>
            context.Resource is Guid doctorId &&
            doctorId != Guid.Empty &&
            context.User.FindAll("schedule_doctor_id").Any(claim =>
                Guid.TryParse(claim.Value, out var allowedDoctorId) &&
                allowedDoctorId == doctorId));
    });
});
builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = "X-CSRF-TOKEN";

    options.Cookie.Name = "__Host-Clinic.Antiforgery";
    options.Cookie.HttpOnly = true;
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.Path = "/";
});
builder.Services.AddSingleton<TimeZoneInfo>(
    TimeZoneInfo.FindSystemTimeZoneById("Asia/Amman"));

builder.Services.AddScoped<
    IWorkingScheduleRepository,
    WorkingScheduleRepository>();
builder.Services.AddScoped<
    IDoctorDayClosureRepository,
    DoctorDayClosureRepository>();

var app = builder.Build();
app.UseExceptionHandler();
app.UseStatusCodePages();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.Run();

public partial class Program
{
}
