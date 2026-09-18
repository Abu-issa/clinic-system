using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Clinic.Application.Storage;
using Clinic.Domain.Entities;
using Clinic.Domain.Enums;
using Clinic.Infrastructure.Persistence;
using Clinic.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Clinic.IntegrationTests.Api;

public sealed partial class StaffAttachmentHttpTests(SqlDatabaseFixture database) : IClassFixture<SqlDatabaseFixture>
{
    private static readonly byte[] PdfBytes = "%PDF-1.7 synthetic clinical attachment"u8.ToArray();

    private sealed class Harness(WebApplicationFactory<Program> factory, AccessAuditHttpTests.FailAuditInsert failure,
        Patient patient, Doctor doctor, Visit visit) : IDisposable
    {
        public WebApplicationFactory<Program> Factory { get; } = factory;
        public AccessAuditHttpTests.FailAuditInsert Failure { get; } = failure;
        public Patient Patient { get; } = patient;
        public Doctor Doctor { get; } = doctor;
        public Visit Visit { get; } = visit;
        public string PatientPath => $"/api/staff/patients/{Patient.Id}";
        public void Dispose() => Factory.Dispose();
    }

    private async Task<Harness> Seed()
    {
        var now = DateTimeOffset.UtcNow;
        var patient = new Patient("SensitiveName", "SensitivePhone");
        var doctor = new Doctor("Synthetic doctor");
        var visit = new Visit(patient.Id, doctor.Id, null, now, "seed", now);
        await using (var db = database.CreateContext())
        {
            db.AddRange(patient, doctor, visit);
            await db.SaveChangesAsync();
        }

        var failure = new AccessAuditHttpTests.FailAuditInsert();
        var factory = new BookingApiFactory(database.ConnectionString).WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
                services.AddDbContext<ClinicDbContext>(options => options.AddInterceptors(failure))));
        return new Harness(factory, failure, patient, doctor, visit);
    }

    private static readonly string[] ReadPermissions = ["attachments.read", "attachments.write"];

    /// <summary>Ticket-backed client. When associateDoctor is set, the persisted staff user's
    /// AssociatedDoctorId is pointed at the seeded doctor (real clinical authority).</summary>
    private async Task<(HttpClient Client, string Actor)> Client(Harness h, string role = "Doctor",
        Guid? patientScope = null, string[]? permissions = null, bool mfa = true, bool associateDoctor = false, Claim[]? extraClaims = null)
    {
        var claims = new List<Claim> { new(ClaimTypes.Role, role),
            new("patient_record_id", (patientScope ?? h.Patient.Id).ToString()) };
        if (extraClaims is not null) claims.AddRange(extraClaims);
        if (mfa) claims.Add(new("amr", "mfa"));
        claims.AddRange((permissions ?? ReadPermissions).Select(p => new Claim("permission", p)));
        var persisted = PersistedTicketStaff.Add(h.Factory, claims).ToList();
        var actor = persisted.Single(x => x.Type == "staff_id").Value;
        persisted.Add(new(ClaimTypes.NameIdentifier, actor));
        if (associateDoctor)
        {
            await using var db = database.CreateContext();
            var user = await db.Users.SingleAsync(x => x.Id == actor);
            user.AssociatedDoctorId = h.Doctor.Id;
            await db.SaveChangesAsync();
        }
        var authOptions = h.Factory.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get("ClinicStaff");
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(persisted, "ClinicStaff")),
            new AuthenticationProperties { IssuedUtc = DateTimeOffset.UtcNow, ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(10) },
            "ClinicStaff");
        var client = h.Factory.CreateClient(new() { BaseAddress = new("https://localhost"), AllowAutoRedirect = false, HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"{authOptions.Cookie.Name}={authOptions.TicketDataFormat.Protect(ticket)}");
        return (client, actor);
    }

    private static async Task<HttpClient> WithCsrf(HttpClient client)
    {
        using var response = await client.GetAsync("/api/staff/auth/csrf");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var token = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement
            .GetProperty("requestToken").GetString();
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", token);
        // The clients intentionally do not run a cookie container; carry the antiforgery
        // cookie explicitly alongside the staff session cookie.
        var setCookie = response.Headers.TryGetValues("Set-Cookie", out var cookies)
            ? cookies.First(c => c.StartsWith("__Host-Clinic.Antiforgery=", StringComparison.Ordinal))
            : null;
        var antiforgeryValue = setCookie?.Split(';', 2)[0];
        if (antiforgeryValue is not null)
        {
            var existing = client.DefaultRequestHeaders.TryGetValues("Cookie", out var current)
                ? current.Single() : null;
            client.DefaultRequestHeaders.Remove("Cookie");
            client.DefaultRequestHeaders.Add("Cookie", $"{existing}; {antiforgeryValue}");
        }
        return client;
    }

    private static MultipartFormDataContent UploadBody(byte[] bytes, string name = "report.pdf",
        string contentType = "application/pdf")
    {
        var body = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        body.Add(file, "file", name);
        return body;
    }

    private async Task<(Guid AttachmentId, string StorageKey)> UploadPdf(HttpClient client, string patientPath,
        Guid? visitId = null)
    {
        using var response = await client.PostAsync(
            visitId is { } v ? $"{patientPath}/visits/{v}/attachments" : $"{patientPath}/attachments",
            UploadBody(PdfBytes));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        var attachmentId = json.GetProperty("attachmentId").GetGuid();
        await using var db = database.CreateContext();
        var storageKey = await db.Set<StoredFile>().AsNoTracking()
            .Where(f => f.Id == db.Set<PatientAttachment>().AsNoTracking()
                .Where(a => a.Id == attachmentId).Select(a => a.StoredFileId).Single())
            .Select(f => f.StorageKey).SingleAsync();
        return (attachmentId, storageKey);
    }

    private static string ProblemCode(HttpResponseMessage response) =>
        JsonDocument.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult())
            .RootElement.GetProperty("code").GetString()!;

    private static string HeaderValue(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.First()
            : response.Content.Headers.TryGetValues(name, out var contentValues) ? contentValues.First()
            : throw new InvalidOperationException($"Header {name} missing.");

    // ---------- UPLOAD ----------

    [Fact]
    public async Task PatientUploadPersistsObjectMetadataLinkAndSingleAuditEvent()
    {
        using var h = await Seed();
        var (client, actor) = await Client(h);
        using var owned = client;
        await WithCsrf(client);

        using var response = await client.PostAsync($"{h.PatientPath}/attachments", UploadBody(PdfBytes));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(body).RootElement;
        var attachmentId = json.GetProperty("attachmentId").GetGuid();
        Assert.Equal("report.pdf", json.GetProperty("originalFileName").GetString());
        Assert.Equal("application/pdf", json.GetProperty("contentType").GetString());
        Assert.Equal(PdfBytes.LongLength, json.GetProperty("sizeBytes").GetInt64());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("visitId").ValueKind);
        // No storage internals in the response.
        Assert.DoesNotContain("storageKey", body);
        Assert.DoesNotContain("sha256", body);
        Assert.DoesNotContain("storedFileId", body);
        Assert.DoesNotContain("createdByStaffId", body);

        await using var verify = database.CreateContext();
        var attachment = await verify.Set<PatientAttachment>().AsNoTracking().SingleAsync(x => x.Id == attachmentId);
        Assert.Equal(h.Patient.Id, attachment.PatientId);
        Assert.Null(attachment.VisitId);
        Assert.Equal(actor, attachment.CreatedByStaffId);
        var file = await verify.Set<StoredFile>().AsNoTracking().SingleAsync(x => x.Id == attachment.StoredFileId);
        Assert.Equal(PdfBytes.LongLength, file.SizeBytes);

        // Exactly one file.upload event with the clinical patient scope and actor.
        var uploadEvent = Assert.Single(await verify.AuditEvents
            .Where(x => x.ActionCode == "file.upload" && x.PatientId == h.Patient.Id).ToListAsync());
        Assert.Equal(attachment.Id.ToString("N"), uploadEvent.ResourceId);
        Assert.Equal("attachment", uploadEvent.ResourceType);
        Assert.Equal(h.Patient.Id, uploadEvent.PatientId);
        Assert.Equal(actor, uploadEvent.ActorStaffId);
        Assert.Equal(AuditOutcome.Succeeded, uploadEvent.Outcome);
        Assert.Empty(uploadEvent.Metadata);

        // Exact bytes preserved behind the opaque key; staging namespace cleaned.
        var storage = h.Factory.Services.CreateScope().ServiceProvider.GetRequiredService<IFileStorage>();
        Assert.True(await storage.ExistsAsync(file.StorageKey));
        await using var stored = (await storage.OpenReadAsync(file.StorageKey))!;
        var roundTripped = new byte[PdfBytes.Length];
        Assert.Equal(PdfBytes.Length, await stored.ReadAsync(roundTripped));
        Assert.Equal(PdfBytes, roundTripped);
    }

    [Fact]
    public async Task VisitUploadRequiresPersistedDoctorAuthority()
    {
        using var h = await Seed();
        var (authorized, _) = await Client(h, associateDoctor: true);
        using var authorizedOwned = authorized;
        await WithCsrf(authorized);
        using var ok = await authorized.PostAsync($"{h.PatientPath}/visits/{h.Visit.Id}/attachments",
            UploadBody(PdfBytes));
        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);

        var (unassociated, _) = await Client(h, associateDoctor: false);
        using var unassociatedOwned = unassociated;
        await WithCsrf(unassociated);
        using var denied = await unassociated.PostAsync($"{h.PatientPath}/visits/{h.Visit.Id}/attachments",
            UploadBody(PdfBytes));
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

        await using var db = database.CreateContext();
        Assert.Equal(1, await db.Set<PatientAttachment>().CountAsync(x => x.VisitId == h.Visit.Id));
        Assert.Equal(1, await db.AuditEvents.CountAsync(x => x.ActionCode == "file.upload" && x.PatientId == h.Patient.Id));
    }

    [Fact]
    public async Task CrossPatientVisitIsHiddenAndStoresNothing()
    {
        using var h = await Seed();
        var (client, _) = await Client(h);
        using var owned = client;
        await WithCsrf(client);

        using var response = await client.PostAsync(
            $"{h.PatientPath}/visits/{Guid.NewGuid()}/attachments", UploadBody(PdfBytes));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("attachment_not_found", ProblemCode(response));
        await using var db = database.CreateContext();
        Assert.False(await db.Set<PatientAttachment>().AnyAsync(x => x.PatientId == h.Patient.Id));
        Assert.False(await db.Set<StoredFile>().AsNoTracking().AnyAsync(x => db.Set<PatientAttachment>().Any(a => a.StoredFileId == x.Id && a.PatientId == h.Patient.Id)));
        Assert.False(await db.AuditEvents.AnyAsync(x => x.ActionCode == "file.upload" && x.PatientId == h.Patient.Id));
        Assert.Equal(0, h.Failure.Attempts); // not even an audit insert was attempted
    }

    [Fact]
    public async Task UnsupportedAndFakedTypesAreRejectedWithoutAnyPersistence()
    {
        using var h = await Seed();
        var (client, _) = await Client(h);
        using var owned = client;
        await WithCsrf(client);

        // HTML bytes presented under a .pdf name and the PDF content type: signature fails.
        using var fakePdf = await client.PostAsync($"{h.PatientPath}/attachments",
            UploadBody("<html>not a pdf</html>"u8.ToArray()));
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, fakePdf.StatusCode);
        Assert.Equal("unsupported_file_type", ProblemCode(fakePdf));

        // Unsupported declared type.
        using var exe = await client.PostAsync($"{h.PatientPath}/attachments",
            UploadBody([0x4D, 0x5A, 0x90, 0x00], "payload.exe", "application/octet-stream"));
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, exe.StatusCode);

        await using var db = database.CreateContext();
        Assert.False(await db.Set<PatientAttachment>().AnyAsync(x => x.PatientId == h.Patient.Id));
        Assert.False(await db.Set<StoredFile>().AsNoTracking().AnyAsync(x => db.Set<PatientAttachment>().Any(a => a.StoredFileId == x.Id && a.PatientId == h.Patient.Id)));
        Assert.False(await db.AuditEvents.AnyAsync(x => x.ActionCode == "file.upload" && x.PatientId == h.Patient.Id));
    }

    [Fact]
    public async Task OversizedUploadIsRejectedAtTheFeatureLimit()
    {
        using var h = await Seed();
        var tiny = new BookingApiFactory(database.ConnectionString).WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Attachments:MaxFileSizeBytes", "64");
            builder.ConfigureTestServices(services =>
                services.AddDbContext<ClinicDbContext>(options => options.AddInterceptors(h.Failure)));
        });
        using var tinyOwned = tiny;
        var (client, _) = await Client(new Harness(tiny, h.Failure, h.Patient, h.Doctor, h.Visit));
        using var clientOwned = client;
        await WithCsrf(client);

        using var tooBig = await client.PostAsync($"{h.PatientPath}/attachments",
            UploadBody("%PDF-"u8.ToArray().Concat(new byte[100]).ToArray()));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooBig.StatusCode);
        Assert.Equal("file_too_large", ProblemCode(tooBig));
        await using var db = database.CreateContext();
        Assert.False(await db.Set<PatientAttachment>().AnyAsync(x => x.PatientId == h.Patient.Id));
        Assert.False(await db.Set<StoredFile>().AsNoTracking().AnyAsync(x => db.Set<PatientAttachment>().Any(a => a.StoredFileId == x.Id && a.PatientId == h.Patient.Id)));
    }

    [Fact]
    public async Task EmptyUploadIsRejected()
    {
        using var h = await Seed();
        var (client, _) = await Client(h);
        using var owned = client;
        await WithCsrf(client);
        using var response = await client.PostAsync($"{h.PatientPath}/attachments",
            UploadBody(Array.Empty<byte>()));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task MissingOrInvalidCsrfStoresNothing()
    {
        using var h = await Seed();
        var (noHeader, _) = await Client(h);
        using var noHeaderOwned = noHeader;
        using var without = await noHeader.PostAsync($"{h.PatientPath}/attachments", UploadBody(PdfBytes));
        Assert.Equal(HttpStatusCode.BadRequest, without.StatusCode);
        Assert.Equal("invalid_csrf_token", ProblemCode(without));

        var (wrong, _) = await Client(h);
        using var wrongOwned = wrong;
        await WithCsrf(wrong);
        wrong.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        wrong.DefaultRequestHeaders.Add("X-CSRF-TOKEN", "forged-token");
        using var withWrong = await wrong.PostAsync($"{h.PatientPath}/attachments", UploadBody(PdfBytes));
        Assert.Equal(HttpStatusCode.BadRequest, withWrong.StatusCode);

        await using var db = database.CreateContext();
        Assert.False(await db.Set<PatientAttachment>().AnyAsync(x => x.PatientId == h.Patient.Id));
        Assert.False(await db.Set<StoredFile>().AsNoTracking().AnyAsync(x => db.Set<PatientAttachment>().Any(a => a.StoredFileId == x.Id && a.PatientId == h.Patient.Id)));
        Assert.False(await db.AuditEvents.AnyAsync(x => x.ActionCode == "file.upload" && x.PatientId == h.Patient.Id));
        Assert.Equal(0, h.Failure.Attempts);
    }

    [Fact]
    public async Task WrongScopeRoleMfaOrPermissionIsDeniedWithoutAuditEvents()
    {
        using var h = await Seed();
        foreach (var (client, _) in new[]
                 {
                     await Client(h, role: "Receptionist"),
                     await Client(h, mfa: false),
                     await Client(h, permissions: []),
                     await Client(h, patientScope: Guid.NewGuid()),
                 })
        {
            using var owned = client;
            await WithCsrf(client);
            using var response = await client.PostAsync($"{h.PatientPath}/attachments", UploadBody(PdfBytes));
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        await using var db = database.CreateContext();
        Assert.False(await db.Set<PatientAttachment>().AnyAsync(x => x.PatientId == h.Patient.Id));
        Assert.False(await db.AuditEvents.AnyAsync(x => x.ActionCode == "file.upload" && x.PatientId == h.Patient.Id) ||
            await db.AuditEvents.AnyAsync(x => x.ActionCode == "file.list" && x.PatientId == h.Patient.Id));
        Assert.Equal(0, h.Failure.Attempts);
    }

    [Fact]
    public async Task AuditInsertFailureRollsBackTheVisibleAttachmentAndCompensatesTheObject()
    {
        using var h = await Seed();
        var (client, _) = await Client(h);
        using var owned = client;
        await WithCsrf(client);
        h.Failure.Enabled = true; // the shared save carries StoredFile + PatientAttachment + file.upload

        using var response = await client.PostAsync($"{h.PatientPath}/attachments", UploadBody(PdfBytes));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("unexpected_error", ProblemCode(response));
        Assert.DoesNotContain("%PDF", await response.Content.ReadAsStringAsync());
        await using var db = database.CreateContext();
        Assert.False(await db.Set<PatientAttachment>().AnyAsync(x => x.PatientId == h.Patient.Id));
        Assert.False(await db.Set<StoredFile>().AsNoTracking().AnyAsync(x => db.Set<PatientAttachment>().Any(a => a.StoredFileId == x.Id && a.PatientId == h.Patient.Id)));
        Assert.False(await db.AuditEvents.AnyAsync(x => x.ActionCode == "file.upload" && x.PatientId == h.Patient.Id));
        Assert.True(h.Failure.Attempts >= 1); // the storage object existed and was compensated
    }

    [Fact]
    public async Task DuplicateIdenticalUploadsAreIndependentAttachments()
    {
        using var h = await Seed();
        var (client, _) = await Client(h);
        using var owned = client;
        await WithCsrf(client);
        var (first, firstKey) = await UploadPdf(client, h.PatientPath);
        var (second, secondKey) = await UploadPdf(client, h.PatientPath);
        Assert.NotEqual(first, second);
        Assert.NotEqual(firstKey, secondKey);
        await using var db = database.CreateContext();
        Assert.Equal(2, await db.Set<PatientAttachment>().AsNoTracking().CountAsync(x => x.PatientId == h.Patient.Id));
        Assert.Equal(2, await db.AuditEvents.CountAsync(x => x.ActionCode == "file.upload" && x.PatientId == h.Patient.Id));
    }

    // ---------- LIST ----------

    [Fact]
    public async Task ListIsAuthorizedPaginatedIsolatedAndFailClosed()
    {
        using var h = await Seed();
        var (client, _) = await Client(h);
        using var owned = client;
        await WithCsrf(client);
        var ids = new List<Guid>();
        for (var i = 0; i < 3; i++) ids.Add((await UploadPdf(client, h.PatientPath)).AttachmentId);

        using var page1 = await client.GetAsync($"{h.PatientPath}/attachments?page=1&pageSize=2");
        Assert.Equal(HttpStatusCode.OK, page1.StatusCode);
        var page1Json = JsonDocument.Parse(await page1.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(2, page1Json.GetProperty("items").GetArrayLength());
        using var page2 = await client.GetAsync($"{h.PatientPath}/attachments?page=2&pageSize=2");
        var page2Json = JsonDocument.Parse(await page2.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(1, page2Json.GetProperty("items").GetArrayLength());
        var listed = page1Json.GetProperty("items").EnumerateArray()
            .Concat(page2Json.GetProperty("items").EnumerateArray())
            .Select(x => x.GetProperty("attachmentId").GetGuid()).OrderBy(x => x).ToArray();
        Assert.Equal(ids.Order(), listed);

        // No storage internals or actors leak.
        var text = await page1.Content.ReadAsStringAsync();
        Assert.DoesNotContain("storageKey", text);
        Assert.DoesNotContain("sha256", text);
        Assert.DoesNotContain("createdByStaffId", text);
        Assert.DoesNotContain("Sensitive", text);

        // Patient isolation: another patient's route is out of scope entirely.
        var (foreign, _) = await Client(h, patientScope: Guid.NewGuid());
        using var foreignOwned = foreign;
        using var denied = await foreign.GetAsync(
            $"/api/staff/patients/{Guid.NewGuid()}/attachments");
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

        // One file.list event per successful list request.
        await using var db = database.CreateContext();
        var listEvents = await db.AuditEvents.Where(x => x.ActionCode == "file.list" && x.PatientId == h.Patient.Id).ToListAsync();
        Assert.Equal(2, listEvents.Count);
        Assert.All(listEvents, e =>
        {
            Assert.Equal("patient", e.ResourceType);
            Assert.Equal(h.Patient.Id.ToString("N"), e.ResourceId);
            Assert.Equal(h.Patient.Id, e.PatientId);
        });

        // Fail-closed: an audit outage prevents disclosure of the list.
        h.Failure.Enabled = true;
        using var failed = await client.GetAsync($"{h.PatientPath}/attachments");
        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        Assert.DoesNotContain("report.pdf", await failed.Content.ReadAsStringAsync());
        h.Failure.Enabled = false;
    }

    [Fact]
    public async Task VisitListAuditsTheVisitAndHidesForeignVisits()
    {
        using var h = await Seed();
        var (client, _) = await Client(h, associateDoctor: true);
        using var owned = client;
        await WithCsrf(client);
        await UploadPdf(client, h.PatientPath, h.Visit.Id);

        using var ok = await client.GetAsync($"{h.PatientPath}/visits/{h.Visit.Id}/attachments");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

        using var foreign = await client.GetAsync($"{h.PatientPath}/visits/{Guid.NewGuid()}/attachments");
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);

        await using var db = database.CreateContext();
        var listEvent = Assert.Single(await db.AuditEvents.Where(x => x.ActionCode == "file.list" && x.PatientId == h.Patient.Id).ToListAsync());
        Assert.Equal("visit", listEvent.ResourceType);
        Assert.Equal(h.Visit.Id.ToString("N"), listEvent.ResourceId);
        Assert.Equal(h.Patient.Id, listEvent.PatientId);
    }

    // ---------- DOWNLOAD ----------

    [Fact]
    public async Task DownloadReturnsExactBytesWithSafeHeadersAndOneAuditEvent()
    {
        using var h = await Seed();
        var (client, _) = await Client(h);
        using var owned = client;
        await WithCsrf(client);
        var (attachmentId, _) = await UploadPdf(client, h.PatientPath);

        // Arabic display name round-trips as RFC 5987 filename* without header injection.
        using var arabicUpload = await client.PostAsync($"{h.PatientPath}/attachments",
            UploadBody(PdfBytes, "تقرير طبي.pdf"));
        Assert.Equal(HttpStatusCode.Created, arabicUpload.StatusCode);
        var arabicId = (await arabicUpload.Content.ReadFromJsonAsync<JsonDocument>())!
            .RootElement.GetProperty("attachmentId").GetGuid();

        using var response = await client.GetAsync($"{h.PatientPath}/attachments/{attachmentId}/download");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(PdfBytes, await response.Content.ReadAsByteArrayAsync());
        Assert.Equal("application/pdf", response.Content.Headers.ContentType?.MediaType);
        var disposition = HeaderValue(response, "Content-Disposition");
        Assert.StartsWith("attachment", disposition);
        Assert.Contains("filename*=", disposition);
        Assert.DoesNotContain("\r", disposition);
        Assert.DoesNotContain("\n", disposition);
        Assert.Equal("nosniff", HeaderValue(response, "X-Content-Type-Options"));
        Assert.True(response.Headers.CacheControl?.NoStore);

        using var arabic = await client.GetAsync($"{h.PatientPath}/attachments/{arabicId}/download");
        Assert.Equal(HttpStatusCode.OK, arabic.StatusCode);
        Assert.Contains(Uri.EscapeDataString("تقرير طبي.pdf"), HeaderValue(arabic, "Content-Disposition"));

        await using var db = database.CreateContext();
        var downloads = await db.AuditEvents.Where(x => x.ActionCode == "file.download" && x.PatientId == h.Patient.Id).ToListAsync();
        Assert.Equal(2, downloads.Count);
        Assert.All(downloads, e =>
        {
            Assert.Equal("attachment", e.ResourceType);
            Assert.Equal(h.Patient.Id, e.PatientId);
            Assert.Empty(e.Metadata);
        });
    }

    [Fact]
    public async Task DownloadAuditFailureReturnsNoBytes()
    {
        using var h = await Seed();
        var (client, _) = await Client(h);
        using var owned = client;
        await WithCsrf(client);
        var (attachmentId, _) = await UploadPdf(client, h.PatientPath);
        h.Failure.Enabled = true;

        using var response = await client.GetAsync($"{h.PatientPath}/attachments/{attachmentId}/download");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal("unexpected_error", ProblemCode(response));
        Assert.DoesNotContain("%PDF", body);
        Assert.DoesNotContain("Sensitive", body);
        Assert.Contains("no-store", HeaderValue(response, "Cache-Control"));
        await using var db = database.CreateContext();
        Assert.Empty(await db.AuditEvents.Where(x => x.ActionCode == "file.download" && x.PatientId == h.Patient.Id).ToListAsync());
    }

    [Fact]
    public async Task MissingBackingObjectReturnsSanitizedControlledFailure()
    {
        using var h = await Seed();
        var (client, _) = await Client(h);
        using var owned = client;
        await WithCsrf(client);
        var (attachmentId, storageKey) = await UploadPdf(client, h.PatientPath);
        var storage = h.Factory.Services.CreateScope().ServiceProvider.GetRequiredService<IFileStorage>();
        await storage.DeleteAsync(storageKey);

        using var response = await client.GetAsync($"{h.PatientPath}/attachments/{attachmentId}/download");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal("backing_object_missing", ProblemCode(response));
        Assert.DoesNotContain(storageKey, body);
        Assert.DoesNotContain("%PDF", body);
        await using var db = database.CreateContext();
        Assert.Empty(await db.AuditEvents.Where(x => x.ActionCode == "file.download" && x.PatientId == h.Patient.Id).ToListAsync());
    }

    [Fact]
    public async Task ForeignPatientAttachmentIsHiddenWithoutAudit()
    {
        // Patient A gets an attachment; the requester is scoped to patient B and asks through
        // B's own route: A's attachment ID must resolve to a hidden 404, never an authorization
        // leak and never a successful audit event.
        using var h = await Seed();
        var (uploader, _) = await Client(h);
        using var uploaderOwned = uploader;
        await WithCsrf(uploader);
        var (attachmentA, _) = await UploadPdf(uploader, h.PatientPath);

        var patientB = new Patient("OtherPatientName", "OtherPhone");
        await using (var db = database.CreateContext())
        {
            db.Add(patientB);
            await db.SaveChangesAsync();
        }
        var pathB = $"/api/staff/patients/{patientB.Id}";
        var (foreign, _) = await Client(h, patientScope: patientB.Id);
        using var foreignOwned = foreign;
        var attemptsBefore = h.Failure.Attempts;

        using var denied = await foreign.GetAsync($"{pathB}/attachments/{attachmentA}/download");
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        Assert.Equal("attachment_not_found", ProblemCode(denied));
        Assert.Equal(attemptsBefore, h.Failure.Attempts); // denied access wrote nothing
        var hiddenBody = await denied.Content.ReadAsStringAsync();
        foreach (var secret in new[] { "report.pdf", "application/pdf", "sizeBytes", "storedFileId", "storageKey", "sha256" })
            Assert.DoesNotContain(secret, hiddenBody);
        Assert.False(denied.Content.Headers.Contains("Content-Disposition"));

        using var listed = await foreign.GetAsync($"{pathB}/attachments");
        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
        Assert.Equal(0, JsonDocument.Parse(await listed.Content.ReadAsStringAsync()).RootElement
            .GetProperty("items").GetArrayLength());
        Assert.Equal(attemptsBefore + 1, h.Failure.Attempts); // exactly one successful list event
    }

    [Fact]
    public async Task RevokedReadPermissionOrScopeInvalidatesTheSessionImmediately()
    {
        using var h = await Seed();
        var (uploader, _) = await Client(h);
        using var uploaderOwned = uploader;
        await WithCsrf(uploader);
        var (attachmentId, _) = await UploadPdf(uploader, h.PatientPath);

        var (reader, readerActor) = await Client(h);
        using var readerOwned = reader;
        await using (var db = database.CreateContext())
        {
            var claim = await db.UserClaims
                .FirstAsync(x => x.UserId == readerActor && x.ClaimType == "permission");
            db.UserClaims.Remove(claim);
            await db.SaveChangesAsync();
        }
        using var denied = await reader.GetAsync($"{h.PatientPath}/attachments/{attachmentId}/download");
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
    }
}
