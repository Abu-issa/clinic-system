using Clinic.Application.Audit;
using Clinic.Api.Audit;
using Clinic.Application.Storage;
using Clinic.Application.Patients;
using Clinic.Application.Visits;
using Clinic.Application.Medications;
using Clinic.Application.Prescriptions;
using Clinic.Infrastructure.Audit;
using Clinic.Infrastructure.Authentication;
using Clinic.Infrastructure.Printing;
using Clinic.Api.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using System.Threading.RateLimiting;
using Clinic.Application.Abstractions;
using Clinic.Application.Appointments;
using Clinic.Infrastructure.Persistence;
using Clinic.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Clinic.Api.ErrorHandling;
using Clinic.Application.Schedules;
using Clinic.Domain.Enums;
using Microsoft.Extensions.Options;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<Clinic.Api.StaffMvc.StaffText>();
builder.Services.AddControllersWithViews().AddJsonOptions(options =>
{
    options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter<AppointmentType>());
    options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter<ClinicalTestCategory>(allowIntegerValues: false));
    options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter<ClinicalTestStatus>(allowIntegerValues: false));
});
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter<AppointmentType>());
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter<ClinicalTestCategory>(allowIntegerValues: false));
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter<ClinicalTestStatus>(allowIntegerValues: false));
});
builder.Services.AddOptions<BookingPolicySettings>()
    .BindConfiguration("BookingPolicy")
    .Validate(settings => settings.IsValid(), "Invalid booking policy durations, interval, notice, or horizon.")
    .ValidateOnStart();
// Printable clinic display details contain no secrets; real values are configured per deployment.
// Development/test deliberately use the shipped clearly-synthetic placeholders; production
// startup refuses them so a real clinic identity decision is unavoidable.
var isProduction = builder.Environment.IsProduction();
builder.Services.AddOptions<PrintClinicDetails>()
    .BindConfiguration("ClinicDisplay")
    .Validate(clinic => clinic.IsValid(),
        "Clinic display name, address, and phone are required and must stay within documented length bounds for printable prescriptions.")
    .Validate(clinic => !isProduction || !PrintClinicDetails.IsSyntheticPlaceholder(clinic),
        "Production cannot start with the shipped synthetic clinic display values; configure the real clinic identity for printable prescriptions.")
    .ValidateOnStart();
builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<PrintClinicDetails>>().Value);
// QuestPDF license selection is an explicit deployment decision (QuestPDF.Settings.License is
// the only supported mechanism): Community, Professional, or Enterprise. Nothing is inherited
// silently; the value must be configured for every environment before PDF rendering exists.
builder.Services.AddOptions<PrintLicenseOptions>()
    .BindConfiguration(PrintLicenseOptions.SectionName)
    .Validate(PrintLicenseOptions.IsConfigured,
        $"PrintLicensing:PdfLicenseType is required and must be one of: {PrintLicenseOptions.AllowedValues}.")
    .ValidateOnStart();
builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<PrintLicenseOptions>>().Value);
// Private file storage (Phase 1 foundation): local filesystem provider only; a production
// object-storage provider is a later explicit decision. Local storage in production requires
// the reviewed FileStorage:AllowLocalInProduction opt-in, and the private root must never sit
// inside wwwroot (where static-file serving could expose clinical bytes).
builder.Services.AddOptions<FileStorageOptions>()
    .BindConfiguration(FileStorageOptions.SectionName)
    .Validate(storage => storage.IsValid(),
        $"FileStorage configuration is invalid: Provider must be 'Local', LocalRoot must be a bounded safe directory path, and MaxFileSizeBytes must be between 1 and {FileStorageOptions.AbsoluteMaxFileSizeBytes}.")
    .Validate(storage => !isProduction || storage.Provider != "Local" || storage.AllowLocalInProduction,
        "Production cannot serve private files from local storage without the explicit FileStorage:AllowLocalInProduction=true opt-in.")
    .Validate(storage =>
    {
        var webRoot = builder.Environment.WebRootPath
            ?? Path.Combine(builder.Environment.ContentRootPath, "wwwroot");
        return storage.IsOutsideWebRoot(webRoot);
    }, "FileStorage:LocalRoot must not be inside wwwroot; stored clinical files are never statically served.")
    .ValidateOnStart();
builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<FileStorageOptions>>().Value);
builder.Services.AddSingleton<Clinic.Application.Storage.IFileStorage, Clinic.Infrastructure.Storage.LocalFileStorage>();
builder.Services.AddScoped<Clinic.Application.Storage.IStoredFileStore, Clinic.Infrastructure.Storage.StoredFileStore>();
builder.Services.AddScoped<Clinic.Application.Storage.StoredFileService>();
// Clinical attachments (Phase 2): narrow Doctor/DoctorAssistant clinical surface with explicit
// persisted permissions; the feature limit must stay within the storage foundation's absolute max.
builder.Services.AddOptions<Clinic.Application.Attachments.AttachmentOptions>()
    .BindConfiguration(Clinic.Application.Attachments.AttachmentOptions.SectionName)
    .Validate(o => o.IsValid(),
        $"Attachments:MaxFileSizeBytes must be between 1 and {FileStorageOptions.AbsoluteMaxFileSizeBytes}.")
    .Validate(o => o.MaxFileSizeBytes <= (builder.Configuration.GetValue<long?>("FileStorage:MaxFileSizeBytes")
            ?? FileStorageOptions.DefaultMaxFileSizeBytes),
        "Attachments:MaxFileSizeBytes must not exceed FileStorage:MaxFileSizeBytes.")
    .ValidateOnStart();
builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<Clinic.Application.Attachments.AttachmentOptions>>().Value);
builder.Services.AddScoped<Clinic.Application.Attachments.IAttachmentStore, Clinic.Infrastructure.Repositories.AttachmentStore>();
builder.Services.AddScoped<Clinic.Application.Attachments.AttachmentService>();
builder.Services.AddScoped<Clinic.Application.ClinicalTests.IClinicalTestStore, ClinicalTestStore>();
builder.Services.AddScoped<Clinic.Application.ClinicalTests.ClinicalTestService>();
builder.Services.AddScoped<Clinic.Application.ClinicalTests.ClinicalTestLifecycleService>();
builder.Services.AddSingleton(provider => new BookingPolicy(
    provider.GetRequiredService<IOptions<BookingPolicySettings>>().Value,
    provider.GetRequiredService<TimeZoneInfo>()));
builder.Services.AddScoped<AppointmentAvailabilityService>();
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
    // Provider exceptions can include a duplicate MRN even when sensitive-data logging is off.
    // Expected conflicts are mapped by services; unexpected failures use the sanitized API handler.
    options.ConfigureWarnings(warnings => warnings.Ignore(
        Microsoft.EntityFrameworkCore.Diagnostics.CoreEventId.SaveChangesFailed,
        Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.CommandError));
});

builder.Services.AddScoped<IPatientRepository, PatientRepository>();
builder.Services.AddScoped<IDoctorRepository, DoctorRepository>();
builder.Services.AddScoped<IAppointmentRepository, AppointmentRepository>();
builder.Services.AddScoped<DoctorDayClosureService>();
builder.Services.AddScoped<AppointmentCancellationService>();
builder.Services.AddScoped<AppointmentReschedulingService>();

builder.Services.AddScoped<IUnitOfWork>(provider =>
    provider.GetRequiredService<ClinicDbContext>());

builder.Services.AddScoped<AppointmentBookingService>();

builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
builder.Services.AddScoped<IBookingTransaction, SqlBookingTransaction>();
builder.Services.AddStaffIdentity();
builder.Services.AddScoped<IPatientRecordsStore, PatientRecordsStore>();
builder.Services.AddScoped<PatientRecordsService>();
builder.Services.AddScoped<IVisitStore, VisitStore>();
builder.Services.AddScoped<VisitService>();
builder.Services.AddScoped<IMedicationCatalogStore, MedicationCatalogStore>();
builder.Services.AddScoped<MedicationCatalogService>();
builder.Services.AddScoped<IPrescriptionStore, PrescriptionStore>();
builder.Services.AddScoped<PrescriptionService>();
builder.Services.AddScoped<IPrescriptionPrintStore, PrescriptionPrintStore>();
builder.Services.AddSingleton<IPrescriptionPdfRenderer>(sp =>
    new QuestPdfPrescriptionRenderer(sp.GetRequiredService<PrintLicenseOptions>()));
builder.Services.AddScoped<PrescriptionPrintService>();
// Both audit writers share the scoped ClinicDbContext; mutations use their business save/transaction.
builder.Services.AddScoped<IAuditEventWriter, AuditEventStore>();
builder.Services.AddScoped<IAuditMutationWriter, AuditEventStore>();
builder.Services.AddScoped<IAccessAuditWriter, AccessAuditWriter>();
builder.Services.AddScoped<IAuditQueryStore, AuditQueryStore>();
builder.Services.AddScoped<HttpAccessAudit>();
builder.Services.AddScoped<StaffCookieEvents>();
builder.Services.AddAuthentication("ClinicStaff")
    .AddCookie("ClinicStaff", options => ConfigureCookie(options, "__Host-Clinic.Staff", 30))
    .AddCookie("ClinicStaffIntermediate", options => ConfigureCookie(options, "__Host-Clinic.StaffIntermediate", 5));
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = 429;
    options.OnRejected = async (context, cancellationToken) =>
    {
        context.HttpContext.Response.Headers.CacheControl = "no-store";
        await Results.Problem(statusCode: 429, title: "Too many authentication requests.",
            extensions: new Dictionary<string, object?> { ["code"] = "rate_limited", ["traceId"] = context.HttpContext.TraceIdentifier })
            .ExecuteAsync(context.HttpContext);
    };
    // A fixed number of hashed IP buckets bounds limiter memory even with hostile source addresses.
    options.AddPolicy("staff-auth", context => RateLimitPartition.GetFixedWindowLimiter(
        (context.Connection.RemoteIpAddress?.ToString().GetHashCode() ?? 0) & 255,
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("AuditRead", policy => policy.RequireAuthenticatedUser().RequireClaim("amr", "mfa")
        .RequireRole("Doctor").RequireAssertion(context => context.User.HasClaim("permission", "audit.patient.read") ||
            context.User.HasClaim("permission", "audit.admin.read")));
    void PatientPolicy(string name, string permission, string[] roles, bool scoped = true)
    {
        options.AddPolicy(name, policy => {
            policy.RequireAuthenticatedUser().RequireClaim("amr", "mfa").RequireRole(roles).RequireClaim("permission", permission);
            if (scoped) policy.RequireAssertion(context => context.Resource is Guid id && id != Guid.Empty &&
                context.User.FindAll("patient_record_id").Any(c => Guid.TryParse(c.Value, out var allowed) && allowed == id));
        });
    }
    PatientPolicy("PatientCreate", "patients.admin.write", ["Doctor", "Receptionist"], false);
    PatientPolicy("PatientAdminRead", "patients.admin.read", ["Doctor", "Receptionist"]);
    PatientPolicy("PatientAdminWrite", "patients.admin.write", ["Doctor", "Receptionist"]);
    PatientPolicy("PatientClinicalRead", "patients.clinical.read", ["Doctor", "DoctorAssistant"]);
    PatientPolicy("PatientClinicalWrite", "patients.clinical.write", ["Doctor"]);
    PatientPolicy("VisitRead", "visits.read", ["Doctor", "DoctorAssistant"]);
    PatientPolicy("VisitWrite", "visits.write", ["Doctor"]);
    PatientPolicy("VisitFinalize", "visits.finalize", ["Doctor"]);
    PatientPolicy("VisitAmend", "visits.amend", ["Doctor"]);
    PatientPolicy("VitalWrite", "vitals.write", ["Doctor", "DoctorAssistant"]);

    // Diagnostic orders: only Doctors create; explicitly authorized assistants may read.
    PatientPolicy("ClinicalTestRead", "tests.read", ["Doctor", "DoctorAssistant"]);
    PatientPolicy("ClinicalTestWrite", "tests.write", ["Doctor"]);
    PatientPolicy("ClinicalTestResultWrite", "tests.write", ["Doctor", "DoctorAssistant"]);

    // Clinical attachments: intentionally narrow — Doctor or DoctorAssistant with explicit
    // persisted attachments.* permission and exact patient scope. Receptionist never gains
    // clinical attachment access; no Admin role exists. Visit-level uploads additionally
    // enforce persisted doctor authority in the controller for Doctors.
    PatientPolicy("AttachmentRead", "attachments.read", ["Doctor", "DoctorAssistant"]);
    PatientPolicy("AttachmentWrite", "attachments.write", ["Doctor", "DoctorAssistant"]);

    // Prescriptions: Doctor only, exact persisted patient scope, explicit operation permission.
    void PrescriptionPolicy(string name, string permission)
    {
        options.AddPolicy(name, policy => {
            policy.RequireAuthenticatedUser().RequireClaim("amr", "mfa").RequireRole("Doctor")
                .RequireClaim("permission", permission);
            policy.RequireAssertion(context => context.Resource is Guid id && id != Guid.Empty &&
                context.User.FindAll("patient_record_id").Any(c => Guid.TryParse(c.Value, out var allowed) && allowed == id));
        });
    }
    PrescriptionPolicy("PrescriptionRead", "prescriptions.read");
    PrescriptionPolicy("PrescriptionWrite", "prescriptions.write");
    PrescriptionPolicy("PrescriptionFinalize", "prescriptions.finalize");
    PrescriptionPolicy("PrescriptionRelease", "prescriptions.release");
    PrescriptionPolicy("PrescriptionCancel", "prescriptions.cancel");

    // Medication catalog is clinic-wide: no patient scope participates. Reading is Doctor or
    // DoctorAssistant; administration is Doctor only. Scheduling scopes never grant it.
    options.AddPolicy("MedicationRead", policy => policy
        .RequireAuthenticatedUser().RequireClaim("amr", "mfa")
        .RequireRole("Doctor", "DoctorAssistant").RequireClaim("permission", "medications.read"));
    options.AddPolicy("MedicationManage", policy => policy
        .RequireAuthenticatedUser().RequireClaim("amr", "mfa")
        .RequireRole("Doctor").RequireClaim("permission", "medications.manage"));
    options.AddPolicy("StaffSession", policy => policy.RequireAuthenticatedUser().RequireRole("Doctor", "Receptionist", "DoctorAssistant").RequireClaim("amr", "mfa"));
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
    options.AddPolicy("ViewDoctorAvailability", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireRole("Doctor", "Receptionist");
        policy.RequireClaim("amr", "mfa");
        policy.RequireClaim("permission", "appointments.availability");
        policy.RequireAssertion(context =>
            context.Resource is Guid doctorId && doctorId != Guid.Empty &&
            context.User.FindAll("appointment_doctor_id").Any(claim =>
                Guid.TryParse(claim.Value, out var allowedDoctorId) && allowedDoctorId == doctorId));
    });
    options.AddPolicy("RescheduleDoctorAppointment", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireRole("Doctor", "Receptionist");
        policy.RequireClaim("amr", "mfa");
        policy.RequireClaim("permission", "appointments.reschedule");
        policy.RequireAssertion(context =>
            context.Resource is Guid doctorId &&
            doctorId != Guid.Empty &&
            context.User.FindAll("appointment_doctor_id").Any(claim =>
                Guid.TryParse(claim.Value, out var allowedDoctorId) &&
                allowedDoctorId == doctorId));
    });
    options.AddPolicy("CancelDoctorAppointment", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireRole("Doctor", "Receptionist");
        policy.RequireClaim("amr", "mfa");
        policy.RequireClaim("permission", "appointments.cancel");

        policy.RequireAssertion(context =>
            context.Resource is Guid doctorId &&
            doctorId != Guid.Empty &&
            context.User.FindAll("appointment_doctor_id").Any(claim =>
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
app.UseStaticFiles();
app.UseWhen(context => context.Request.Path.StartsWithSegments("/staff") ||
    Clinic.Api.StaffMvc.StaffUiErrors.IsStaffDownload(context), staff =>
{
    staff.UseRequestLocalization(new RequestLocalizationOptions()
        .SetDefaultCulture("ar").AddSupportedCultures("ar", "en").AddSupportedUICultures("ar", "en"));
    staff.Use(async (context, next) =>
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers.ContentSecurityPolicy = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; object-src 'none'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'";
        try { await next(); }
        catch (Exception ex) when (!context.Response.HasStarted && !context.RequestAborted.IsCancellationRequested)
        {
            // Log only the exception type: provider messages may contain clinical data.
            context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("StaffMvc")
                .LogError("Staff page failed ({ExceptionType}).", ex.GetType().Name);
            await Clinic.Api.StaffMvc.StaffUiErrors.WriteAsync(context, 500);
        }
    });
    staff.UseStatusCodePages(context => Clinic.Api.StaffMvc.StaffUiErrors.WriteAsync(
        context.HttpContext, context.HttpContext.Response.StatusCode));
});

app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api/staff/auth") || context.Request.Path.StartsWithSegments("/api/staff/patients")
        || context.Request.Path.StartsWithSegments("/api/staff/medications")
        || context.Request.Path.StartsWithSegments("/api/staff/audit-events"))
        context.Response.Headers.CacheControl = "no-store";
    await next();
});
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapControllers();
if (args.Length > 0 && args[0] == "staff")
{
    await StaffLocalCommand.RunAsync(args, app.Services);
    return;
}
app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");
app.Run();

static void ConfigureCookie(CookieAuthenticationOptions options, string name, int minutes)
{
    options.Cookie.Name = name;
    options.Cookie.HttpOnly = true;
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.Path = "/";
    options.ExpireTimeSpan = TimeSpan.FromMinutes(minutes);
    options.SlidingExpiration = false;
    options.EventsType = typeof(StaffCookieEvents);
}

public partial class Program
{
}
