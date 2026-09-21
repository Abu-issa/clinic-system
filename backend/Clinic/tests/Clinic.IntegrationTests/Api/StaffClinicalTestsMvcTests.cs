using System.Net;
using System.Text.RegularExpressions;
using Clinic.Domain.Entities;
using Clinic.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Clinic.IntegrationTests.Api;

public sealed partial class StaffClinicalTestsHttpTests
{
    private static string MvcPath(Harness h, Guid? id = null) => $"/staff/patients/{h.Patient.Id}/tests" + (id is null ? "" : $"/{id}");
    private static FormUrlEncodedContent MvcCreateBody(Guid? visit = null, string? token = null, string? submission = null) => new(new Dictionary<string, string> {
        ["SubmissionToken"] = submission ?? "",
        ["Category"] = "Lab", ["TestName"] = "MVC فحص <script>alert(1)</script>",
        ["ClinicalInstructions"] = "SensitiveInstructions", ["VisitId"] = visit?.ToString() ?? "",
        ["__RequestVerificationToken"] = token ?? "" });
    private static FormUrlEncodedContent MvcVersion(byte[] version) => new(new Dictionary<string, string> {
        ["expectedRowVersion"] = Convert.ToBase64String(version) });
    private static void MvcCookies(HttpClient client, HttpResponseMessage response)
    {
        var cookies = client.DefaultRequestHeaders.GetValues("Cookie").Single().Split(';', StringSplitOptions.TrimEntries)
            .Select(c => c.Split('=', 2)).ToDictionary(c => c[0], c => c[1]);
        if (response.Headers.TryGetValues("Set-Cookie", out var values))
            foreach (var value in values) { var c = value.Split(';', 2)[0].Split('=', 2); cookies[c[0]] = c[1]; }
        client.DefaultRequestHeaders.Remove("Cookie");
        client.DefaultRequestHeaders.Add("Cookie", string.Join("; ", cookies.Select(c => c.Key + "=" + c.Value)));
    }
    private static async Task<string> MvcFollow(HttpClient client, HttpResponseMessage response, string notice)
    {
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/staff/patients/", response.Headers.Location!.OriginalString);
        MvcCookies(client, response);
        using var page = await client.GetAsync(response.Headers.Location);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        MvcCookies(client, page);
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains($"data-notice=\"{notice}\"", html);
        return html;
    }

    [Theory]
    [InlineData("Doctor")]
    [InlineData("DoctorAssistant")]
    public async Task MvcListAndDetailAreScopedLocalizedEncodedAndAudited(string role)
    {
        using var h = await Seed(); var (writer, _) = await Client(h, permissions: ResultPermissions);
        using var ownedWriter = writer; await WithCsrf(writer); var id = await CreateOrder(writer, h);
        using var upload = await UploadResult(writer, h, id, await Version(id)); Assert.Equal(HttpStatusCode.OK, upload.StatusCode);
        var (reader, actor) = await Client(h, role, permissions: ["tests.read"]); using var ownedReader = reader;
        using var list = await reader.GetAsync(MvcPath(h));
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var html = await list.Content.ReadAsStringAsync();
        Assert.Contains("lang=\"ar\" dir=\"rtl\"", html); Assert.Contains("SensitiveName", html);
        Assert.DoesNotContain("SensitivePhone", html); Assert.DoesNotContain("result.pdf", html);
        Assert.DoesNotContain("SensitiveInstructions", html); Assert.True(list.Headers.CacheControl!.NoStore);
        Assert.Contains("frame-ancestors 'none'", list.Headers.GetValues("Content-Security-Policy").Single());
        using var detail = await reader.GetAsync(MvcPath(h, id) + "?culture=en");
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode); html = await detail.Content.ReadAsStringAsync();
        Assert.Contains("lang=\"en\" dir=\"ltr\"", html); Assert.Contains("SensitiveInstructions", html);
        Assert.Contains("Result files", html); Assert.Contains("(1)", html);
        Assert.DoesNotContain("result.pdf", html); Assert.DoesNotContain("/attachments/", html);
        Assert.DoesNotContain("name=\"expectedRowVersion\"", html);
        await using var db = database.CreateContext();
        var attachment = await db.PatientAttachments.SingleAsync(x => x.PatientId == h.Patient.Id);
        Assert.DoesNotContain(attachment.Id.ToString(), html);
        var events = await db.AuditEvents.Where(x => x.ActorStaffId == actor).ToArrayAsync();
        Assert.Equal(2, events.Length); Assert.Single(events, e => e.ActionCode == "test-request.list");
        Assert.Single(events, e => e.ActionCode == "test-request.read");
    }

    [Theory]
    [InlineData("Receptionist", true, true, true)]
    [InlineData("Doctor", false, true, true)]
    [InlineData("Doctor", true, false, true)]
    [InlineData("Doctor", true, true, false)]
    public async Task MvcReadsRejectRoleMfaPermissionAndScope(string role, bool mfa, bool permission, bool scope)
    {
        using var h = await Seed(); var (client, _) = await Client(h, role,
            scope ? h.Patient.Id : Guid.NewGuid(), permission ? ["tests.read"] : [], mfa);
        using var owned = client;
        foreach (var path in new[] { MvcPath(h), MvcPath(h, Guid.NewGuid()), MvcPath(h) + "/create" })
        {
            using var response = await client.GetAsync(path + "?culture=en");
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal("text/html", response.Content.Headers.ContentType!.MediaType);
            Assert.DoesNotContain("Sensitive", await response.Content.ReadAsStringAsync());
        }
        await AssertNoMutation(h);
    }

    [Theory]
    [InlineData("page=0")]
    [InlineData("page=1001")]
    [InlineData("pageSize=101")]
    [InlineData("category=Unknown")]
    public async Task MvcListBoundsRejectInvalidQueries(string query)
    {
        using var h = await Seed(); var (client, _) = await Client(h); using var owned = client;
        using var response = await client.GetAsync(MvcPath(h) + "?" + query);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); await AssertNoMutation(h);
    }

    [Fact]
    public async Task MvcCreateUsesActualFormCsrfAndServiceAuthorityWithOneMutationAudit()
    {
        using var h = await Seed(); var (client, actor) = await Client(h, permissions: [.. ResultPermissions, "visits.read"]);
        using var owned = client;
        using var form = await client.GetAsync(MvcPath(h) + "/create?culture=en");
        Assert.Equal(HttpStatusCode.OK, form.StatusCode); MvcCookies(client, form);
        var html = await form.Content.ReadAsStringAsync();
        Assert.Contains(h.Visit.Id.ToString(), html);
        var token = WebUtility.HtmlDecode(Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value);
        Assert.NotEmpty(token);
        var submission = Regex.Match(html, "name=\"SubmissionToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        using var response = await client.PostAsync(MvcPath(h) + "/create?culture=en", MvcCreateBody(h.Visit.Id, token, submission));
        html = await MvcFollow(client, response, "Saved");
        Assert.DoesNotContain("<script>alert(1)</script>", html); Assert.Contains("&lt;script&gt;", html);
        await using var db = database.CreateContext();
        var request = await db.ClinicalTestRequests.SingleAsync(x => x.PatientId == h.Patient.Id);
        Assert.Equal(h.Doctor.Id, request.RequestedByDoctorId); Assert.Equal(h.Visit.Id, request.VisitId);
        Assert.Equal(ClinicalTestStatus.Requested, request.Status); Assert.Equal(ServerNow, request.RequestedAtUtc);
        Assert.Single(await db.AuditEvents.Where(x => x.ActorStaffId == actor && x.ActionCode == "test-request.create").ToArrayAsync());
    }

    [Theory]
    [InlineData("assistant", HttpStatusCode.Forbidden)]
    [InlineData("csrf", HttpStatusCode.BadRequest)]
    [InlineData("cross-visit", HttpStatusCode.NotFound)]
    [InlineData("authority", HttpStatusCode.Forbidden)]
    public async Task MvcCreateRejectsUnauthorizedOrUnverifiedInput(string scenario, HttpStatusCode status)
    {
        using var h = await Seed(); var (client, actor) = await Client(h, scenario == "assistant" ? "DoctorAssistant" : "Doctor",
            associateDoctor: scenario != "authority"); using var owned = client;
        if (scenario != "csrf") await WithCsrf(client);
        Guid? visitId = null;
        if (scenario == "cross-visit")
        {
            var patient = new Patient("Other patient", "Other phone");
            var visit = new Visit(patient.Id, h.Doctor.Id, null, ServerNow, "seed", ServerNow);
            await using var db = database.CreateContext(); db.AddRange(patient, visit); await db.SaveChangesAsync(); visitId = visit.Id;
        }
        var submission = h.Factory.Services.GetRequiredService<Clinic.Api.StaffMvc.StaffCreateSubmissions>().Issue(actor, h.Patient.Id);
        using var response = await client.PostAsync(MvcPath(h) + "/create", MvcCreateBody(visitId, submission: submission));
        Assert.Equal(status, response.StatusCode); await AssertNoMutation(h);
    }

    [Fact]
    public async Task MvcVisitPickerFiltersPatientAndLiveDoctorAndRequiresVisitRead()
    {
        using var h = await Seed(); var other = new Patient("Hidden patient", "Hidden phone"); var doctor = new Doctor("Hidden doctor");
        var otherPatientVisit = new Visit(other.Id, h.Doctor.Id, null, ServerNow, "seed", ServerNow);
        var otherDoctorVisit = new Visit(h.Patient.Id, doctor.Id, null, ServerNow, "seed", ServerNow);
        await using (var db = database.CreateContext()) { db.AddRange(other, doctor, otherPatientVisit, otherDoctorVisit); await db.SaveChangesAsync(); }
        foreach (var canRead in new[] { true, false })
        {
            var (client, _) = await Client(h, permissions: canRead ? [.. ReadPermissions, "visits.read"] : ReadPermissions); using var owned = client;
            using var response = await client.GetAsync(MvcPath(h) + "/create"); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var html = await response.Content.ReadAsStringAsync();
            Assert.Equal(canRead, html.Contains(h.Visit.Id.ToString()));
            Assert.DoesNotContain(otherPatientVisit.Id.ToString(), html); Assert.DoesNotContain(otherDoctorVisit.Id.ToString(), html);
        }
    }

    [Theory]
    [InlineData("Doctor")]
    [InlineData("DoctorAssistant")]
    public async Task MvcUploadUsesServiceAndNewVersionWithSingleUploadAudits(string role)
    {
        using var h = await Seed(); var (writer, _) = await Client(h); using var writerOwned = writer;
        await WithCsrf(writer); var id = await CreateOrder(writer, h);
        var (client, actor) = await Client(h, role, permissions: ResultPermissions); using var owned = client; await WithCsrf(client);
        var original = await Version(id);
        using var response = await client.PostAsync(MvcPath(h, id) + "/results?culture=en", ResultBody(original));
        var html = await MvcFollow(client, response, "Saved");
        Assert.Contains("result.pdf", html); Assert.Contains("/attachments/", html); Assert.Contains("Uploaded", html);
        // Upload stays gated in raw HTML: button disabled and a visible no-JS fallback that
        // tests.js hides/enables only after it actually initializes.
        Assert.Contains("disabled data-upload-button", html);
        Assert.Contains("data-upload-unavailable", html);
        Assert.Contains("<p role=\"status\"", html);
        Assert.Contains(WebUtility.HtmlEncode(Convert.ToBase64String(await Version(id))), html);
        Assert.DoesNotContain(Convert.ToBase64String(original), html);
        Assert.Equal(role == "Doctor", html.Contains("/review/start"));
        foreach (var hidden in new[] { "StorageKey", "Sha256", "CreatedByStaffId" }) Assert.DoesNotContain(hidden, html);
        await using var db = database.CreateContext(); var events = await db.AuditEvents.Where(x => x.ActorStaffId == actor).ToArrayAsync();
        Assert.Equal(3, events.Length); Assert.Single(events, e => e.ActionCode == "file.upload");
        Assert.Single(events, e => e.ActionCode == "test-request.result.upload"); Assert.Single(events, e => e.ActionCode == "test-request.read");
    }

    [Theory]
    [InlineData("type", "Unsupported")]
    [InlineData("stale", "Conflict")]
    [InlineData("multiple", "Invalid")]
    [InlineData("reviewed", "Transition")]
    public async Task MvcUploadErrorsRedirectWithoutAdditionalMutation(string scenario, string notice)
    {
        using var h = await Seed(); var (client, _) = await Client(h, permissions: ResultPermissions); using var owned = client;
        await WithCsrf(client); var id = await CreateOrder(client, h); var original = await Version(id);
        if (scenario is "stale" or "reviewed")
        {
            using var upload = await UploadResult(client, h, id, original); Assert.Equal(HttpStatusCode.OK, upload.StatusCode);
            if (scenario == "reviewed")
            {
                using var start = await ReviewResult(client, h, id, await Version(id)); Assert.Equal(HttpStatusCode.OK, start.StatusCode);
                using var complete = await ReviewResult(client, h, id, await Version(id), true); Assert.Equal(HttpStatusCode.OK, complete.StatusCode);
            }
        }
        var before = await Version(id);
        using var response = await client.PostAsync(MvcPath(h, id) + "/results?culture=en",
            scenario == "type" ? ResultBody(original, "not a pdf"u8.ToArray()) : ResultBody(original, files: scenario == "multiple" ? 2 : 1));
        var html = await MvcFollow(client, response, notice);
        if (scenario == "stale") Assert.Contains("The test request changed. Refresh and try again.", html);
        if (scenario == "reviewed") Assert.DoesNotContain("data-upload ", html);
        Assert.Equal(before, await Version(id));
        await using var db = database.CreateContext();
        Assert.Equal(scenario is "stale" or "reviewed" ? 1 : 0, await db.PatientAttachments.CountAsync(x => x.PatientId == h.Patient.Id));
    }

    [Theory]
    [InlineData("results")]
    [InlineData("review/start")]
    [InlineData("review/complete")]
    public async Task MvcCsrfRejectsAllResultMutations(string action)
    {
        using var h = await Seed(); var (client, _) = await Client(h, permissions: ResultPermissions); using var owned = client;
        await WithCsrf(client); var id = await CreateOrder(client, h); client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        using var response = await client.PostAsync(MvcPath(h, id) + "/" + action,
            action == "results" ? ResultBody(await Version(id)) : MvcVersion(await Version(id)));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); await NoResults(h, id);
    }

    [Fact]
    public async Task MvcReviewTransitionsAndStaleTokensKeepSingleAuditPerMutation()
    {
        using var h = await Seed(); var (client, actor) = await Client(h, permissions: ResultPermissions); using var owned = client;
        await WithCsrf(client); var id = await CreateOrder(client, h); var requested = await Version(id);
        using var invalid = await client.PostAsync(MvcPath(h, id) + "/review/start?culture=en", MvcVersion(requested));
        await MvcFollow(client, invalid, "Transition"); Assert.Equal(requested, await Version(id));
        using var upload = await UploadResult(client, h, id, requested); Assert.Equal(HttpStatusCode.OK, upload.StatusCode);
        foreach (var complete in new[] { false, true })
        {
            var path = MvcPath(h, id) + (complete ? "/review/complete" : "/review/start") + "?culture=en";
            using var stale = await client.PostAsync(path, MvcVersion(requested)); await MvcFollow(client, stale, "Conflict");
            using var success = await client.PostAsync(path, MvcVersion(await Version(id)));
            var html = await MvcFollow(client, success, "Saved");
            if (complete) { Assert.Contains("This request is reviewed", html); Assert.DoesNotContain("name=\"expectedRowVersion\"", html); }
            else Assert.Contains("/review/complete", html);
        }
        await using var db = database.CreateContext(); var events = await db.AuditEvents.Where(x => x.ActorStaffId == actor).ToArrayAsync();
        Assert.Single(events, e => e.ActionCode == "test-request.review.start"); Assert.Single(events, e => e.ActionCode == "test-request.review.complete");
    }

    [Theory]
    [InlineData("start")]
    [InlineData("complete")]
    public async Task MvcAssistantCannotReview(string command)
    {
        using var h = await Seed(); var (writer, _) = await Client(h, permissions: ResultPermissions); using var ow = writer; await WithCsrf(writer);
        var id = await CreateOrder(writer, h); using var upload = await UploadResult(writer, h, id, await Version(id));
        Assert.Equal(HttpStatusCode.OK, upload.StatusCode);
        var (client, _) = await Client(h, "DoctorAssistant", permissions: ResultPermissions); using var owned = client; await WithCsrf(client);
        using var response = await client.PostAsync(MvcPath(h, id) + "/review/" + command, MvcVersion(await Version(id)));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task MvcDownloadLinksReuseProtectedAuditedEndpoint()
    {
        using var h = await Seed(); var (client, actor) = await Client(h, permissions: ResultPermissions); using var owned = client;
        await WithCsrf(client); var id = await CreateOrder(client, h); using var upload = await UploadResult(client, h, id, await Version(id));
        Assert.Equal(HttpStatusCode.OK, upload.StatusCode);
        using var detail = await client.GetAsync(MvcPath(h, id)); var html = await detail.Content.ReadAsStringAsync();
        var url = WebUtility.HtmlDecode(Regex.Match(html, "href=\"(/api/staff/patients/[^\"]+/download[^\"]*)\"").Groups[1].Value); Assert.NotEmpty(url);
        using var download = await client.GetAsync(url); Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal(ResultPdf, await download.Content.ReadAsByteArrayAsync());
        Assert.Equal("attachment", download.Content.Headers.ContentDisposition!.DispositionType);
        var (limited, _) = await Client(h, permissions: ["tests.read"]); using var limitedOwned = limited;
        using var denied = await limited.GetAsync(url); Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using var other = await Seed(); var (otherClient, _) = await Client(other, permissions: ResultPermissions); using var otherOwned = otherClient;
        using var wrongScope = await otherClient.GetAsync(url); Assert.Equal(HttpStatusCode.Forbidden, wrongScope.StatusCode);
        using var wrongPatient = await otherClient.GetAsync(url.Replace(h.Patient.Id.ToString(), other.Patient.Id.ToString()));
        Assert.Equal(HttpStatusCode.NotFound, wrongPatient.StatusCode);
        await using var db = database.CreateContext();
        Assert.Single(await db.AuditEvents.Where(x => x.ActorStaffId == actor && x.ActionCode == "file.download").ToArrayAsync());
    }

    [Theory]
    [InlineData("list")]
    [InlineData("detail")]
    [InlineData("create")]
    public async Task MvcReadAuditFailureDisclosesNoPatientOrTestContent(string page)
    {
        using var h = await Seed(); var (client, _) = await Client(h, permissions: ResultPermissions); using var owned = client;
        await WithCsrf(client); var id = await CreateOrder(client, h); h.Failure.Enabled = true;
        using var response = await client.GetAsync(page == "list" ? MvcPath(h) : page == "create" ? MvcPath(h) + "/create" : MvcPath(h, id));
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Equal("text/html", response.Content.Headers.ContentType!.MediaType);
        Assert.DoesNotContain("Sensitive", html); Assert.DoesNotContain(id.ToString(), html); Assert.DoesNotContain("Sql", html);
    }

    [Theory]
    [InlineData("csrf", HttpStatusCode.BadRequest)]
    [InlineData("permission", HttpStatusCode.Forbidden)]
    [InlineData("scope", HttpStatusCode.Forbidden)]
    [InlineData("receptionist", HttpStatusCode.Forbidden)]
    [InlineData("authority", HttpStatusCode.Forbidden)]
    [InlineData("request", HttpStatusCode.NotFound)]
    public async Task MvcUploadGateRejectsBeforeReadingMultipart(string reason, HttpStatusCode status)
    {
        using var seed = await Seed(); var meter = new StaffAttachmentHttpTests.BodyMeter();
        using var factory = seed.Factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<IStartupFilter>(meter)));
        using var h = new Harness(factory, seed.Failure, seed.Patient, seed.Doctor, seed.Visit);
        var (writer, _) = await Client(h); using var ow = writer; await WithCsrf(writer); var id = await CreateOrder(writer, h, h.Visit.Id);
        var (client, _) = await Client(h, reason == "receptionist" ? "Receptionist" : "Doctor",
            reason == "scope" ? Guid.NewGuid() : h.Patient.Id,
            reason == "permission" ? ["tests.read", "tests.write"] : ResultPermissions,
            associateDoctor: reason != "authority"); using var owned = client;
        if (reason != "csrf") await WithCsrf(client);
        meter.Bytes = 0;
        using var response = await client.PostAsync(MvcPath(h, reason == "request" ? Guid.NewGuid() : id) + "/results", ResultBody(await Version(id)));
        Assert.Equal(status, response.StatusCode); Assert.Equal(0, meter.Bytes); await NoResults(h, id);
    }

    [Fact]
    public async Task MvcUploadSizeLimitRedirectsWithSafeNotice()
    {
        using var seed = await Seed();
        using var factory = seed.Factory.WithWebHostBuilder(b => b.UseSetting("Attachments:MaxFileSizeBytes", "64"));
        using var h = new Harness(factory, seed.Failure, seed.Patient, seed.Doctor, seed.Visit);
        var (client, _) = await Client(h, permissions: ResultPermissions); using var owned = client; await WithCsrf(client);
        var id = await CreateOrder(client, h);
        foreach (var size in new[] { 128, 100000 })
        {
            using var response = await client.PostAsync(MvcPath(h, id) + "/results?culture=en",
                ResultBody(await Version(id), "%PDF-"u8.ToArray().Concat(new byte[size]).ToArray()));
            await MvcFollow(client, response, "TooLarge"); await NoResults(h, id);
        }
    }

    [Fact]
    public async Task MvcExistingCookieLosesAccessAfterPersistedGrantRemoval()
    {
        using var h = await Seed(); var (client, actor) = await Client(h); using var owned = client;
        using var allowed = await client.GetAsync(MvcPath(h)); Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        await using (var db = database.CreateContext())
        {
            db.Remove(await db.UserClaims.SingleAsync(x => x.UserId == actor && x.ClaimValue == "tests.read"));
            await db.SaveChangesAsync();
        }
        using var denied = await client.GetAsync(MvcPath(h) + "?culture=en");
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        Assert.Contains("MFA-verified staff session", await denied.Content.ReadAsStringAsync());
        Assert.DoesNotContain("SensitiveName", await denied.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task MvcMutationAuditFailureRollsBackAndReturnsSafeHtml()
    {
        using var h = await Seed(); var (client, _) = await Client(h, permissions: ResultPermissions); using var owned = client;
        await WithCsrf(client); var id = await CreateOrder(client, h); var version = await Version(id);
        h.Failure.Enabled = true;
        using var response = await client.PostAsync(MvcPath(h, id) + "/results?culture=en", ResultBody(version));
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync(); Assert.Contains("Check the request before resubmitting", html);
        Assert.DoesNotContain("Sensitive", html); Assert.DoesNotContain("result.pdf", html);
        await NoResults(h, id); Assert.Equal(version, await Version(id));
    }

    [Fact]
    public async Task MvcRenderedFormsUseLocalAssetsAndFreshTokensInBothLanguages()
    {
        using var h = await Seed(); var (client, _) = await Client(h, permissions: [.. ResultPermissions, "visits.read"]);
        using var owned = client; await WithCsrf(client); var id = await CreateOrder(client, h, h.Visit.Id);
        using var upload = await UploadResult(client, h, id, await Version(id)); Assert.Equal(HttpStatusCode.OK, upload.StatusCode);
        foreach (var culture in new[] { "ar", "en" })
        foreach (var page in new[] { "list", "create", "detail" })
        {
            var path = page == "list" ? MvcPath(h) : page == "create" ? MvcPath(h) + "/create" : MvcPath(h, id);
            using var response = await client.GetAsync(path + "?culture=" + culture); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var html = await response.Content.ReadAsStringAsync();
            Assert.Contains(culture == "ar" ? "bootstrap.rtl.min.css" : "bootstrap.min.css", html);
            Assert.DoesNotContain("cdn.jsdelivr", html);
            if (page == "detail")
            {
                Assert.Contains("enctype=\"multipart/form-data\"", html);
                Assert.Contains("name=\"__RequestVerificationToken\"", html);
                Assert.Contains("/review/start?culture=" + culture, html);
                Assert.Contains(WebUtility.HtmlEncode(Convert.ToBase64String(await Version(id))), html);
            }
            // Opt-in export of synthetic fixture HTML for local visual QA; never runs on clinic data.
            if (Environment.GetEnvironmentVariable("CLINIC_MVC_SNAPSHOT_DIR") is { Length: > 0 } directory)
            {
                Directory.CreateDirectory(directory);
                var safeHtml = Regex.Replace(html, "(name=\"__RequestVerificationToken\"[^>]*value=\")[^\"]+", "$1synthetic-preview");
                await File.WriteAllTextAsync(Path.Combine(directory, page + "-" + culture + ".html"), safeHtml);
            }
        }
        foreach (var asset in new[] { "/vendor/bootstrap/bootstrap.min.css", "/vendor/bootstrap/bootstrap.rtl.min.css", "/staff-assets/tests.css", "/staff-assets/tests.js" })
        {
            using var response = await client.GetAsync(asset); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.NotEmpty(await response.Content.ReadAsStringAsync());
        }
    }
}
