using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Clinic.Domain.Entities;
using Clinic.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Hosting;

namespace Clinic.IntegrationTests.Api;

public sealed partial class StaffClinicalTestsHttpTests
{
    [Fact]
    public async Task MvcHardeningDevelopmentHttpsSmokeUsesSyntheticDatabaseAndRealAssets()
    {
        using var seed = await Seed();
        using var factory = seed.Factory.WithWebHostBuilder(b => b.UseEnvironment("Development"));
        factory.UseKestrel(options => options.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps()));
        using var h = new Harness(factory, seed.Failure, seed.Patient, seed.Doctor, seed.Visit);
        var (client, _) = await Client(h, permissions: [.. ResultPermissions, "visits.read"]); using var owned = client;
        var address = factory.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        client.BaseAddress = new Uri(address.Replace("127.0.0.1", "localhost"));
        Assert.Equal("https", client.BaseAddress.Scheme);
        Assert.Equal("Development", factory.Services.GetRequiredService<IHostEnvironment>().EnvironmentName);
        await WithCsrf(client);
        using var create = await client.PostAsJsonAsync(h.PatientPath + "/test-requests", Body(h.Visit.Id,
            name: "فحص مختبري طويل لاختبار عرض النتائج ومراجعة الطبيب مع محتوى مختلط CBC / Imaging"));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var id = (await create.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("id").GetGuid();
        foreach (var state in new[] { "Requested", "Uploaded", "UnderReview", "Reviewed" })
        {
            if (state == "Uploaded") { using var result = await client.PostAsync(ResultPath(h, id) + "/results",
                ResultBody(await Version(id), filename: "نتيجة-الفحص-المختبري-للمراجعة-الطبية-اسم-طويل-لاختبار-التفاف-النص-بأمان-report.pdf")); Assert.Equal(HttpStatusCode.OK, result.StatusCode); }
            if (state is "UnderReview" or "Reviewed") { using var result = await ReviewResult(client, h, id, await Version(id), state == "Reviewed"); Assert.Equal(HttpStatusCode.OK, result.StatusCode); }
            using var detail = await client.GetAsync(MvcPath(h, id)); Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
            var html = await detail.Content.ReadAsStringAsync(); Assert.Contains("lang=\"ar\" dir=\"rtl\"", html);
            Assert.Equal(state == "Uploaded", html.Contains("/review/start")); Assert.Equal(state == "UnderReview", html.Contains("/review/complete"));
            Assert.Equal(state != "Reviewed", html.Contains("data-upload-button"));
            if (state == "Uploaded") await ExportSmoke("detail-ar", html);
        }
        foreach (var page in new[] { "list-ar", "create-en" })
        {
            using var response = await client.GetAsync(MvcPath(h) + (page == "create-en" ? "/create?culture=en" : ""));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode); var html = await response.Content.ReadAsStringAsync();
            Assert.Contains(page == "create-en" ? "lang=\"en\" dir=\"ltr\"" : "lang=\"ar\" dir=\"rtl\"", html);
            if (page == "create-en") { Assert.Contains("name=\"SubmissionToken\"", html); Assert.Contains("name=\"__RequestVerificationToken\"", html); }
            await ExportSmoke(page, html);
        }
        foreach (var asset in new[] { "/vendor/bootstrap/bootstrap.min.css", "/vendor/bootstrap/bootstrap.rtl.min.css", "/staff-assets/tests.css", "/staff-assets/tests.js" })
        {
            using var response = await client.GetAsync(asset); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync(); Assert.NotEmpty(body);
            if (asset.Contains("bootstrap")) Assert.Matches("Bootstrap\\s+v5\\.3\\.8", body);
        }
    }

    private static async Task ExportSmoke(string name, string html)
    {
        if (Environment.GetEnvironmentVariable("CLINIC_MVC_SNAPSHOT_DIR") is not { Length: > 0 } directory) return;
        Directory.CreateDirectory(directory);
        html = Regex.Replace(html, "(name=\"(?:__RequestVerificationToken|SubmissionToken)\"[^>]*value=\")[^\"]+", "$1synthetic-preview");
        await File.WriteAllTextAsync(Path.Combine(directory, "smoke-" + name + ".html"), html);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MvcHardeningReviewRechecksPersistedDoctorAuthority(bool complete)
    {
        using var h = await Seed(); var (client, actor) = await Client(h, permissions: ResultPermissions); using var owned = client; await WithCsrf(client);
        var id = await CreateOrder(client, h, h.Visit.Id); using var upload = await UploadResult(client, h, id, await Version(id));
        Assert.Equal(HttpStatusCode.OK, upload.StatusCode);
        if (complete) { using var start = await ReviewResult(client, h, id, await Version(id)); Assert.Equal(HttpStatusCode.OK, start.StatusCode); }
        var before = await Version(id);
        await using (var db = database.CreateContext()) { (await db.Users.SingleAsync(x => x.Id == actor)).AssociatedDoctorId = null; await db.SaveChangesAsync(); }
        using var response = await client.PostAsync(MvcPath(h, id) + (complete ? "/review/complete" : "/review/start"), MvcVersion(before));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode); Assert.Equal(before, await Version(id));
    }

    [Theory]
    [InlineData("actor")]
    [InlineData("patient")]
    [InlineData("missing")]
    public async Task MvcHardeningCreateFormGuardIsBoundToActorAndPatient(string reason)
    {
        using var h = await Seed(); var (client, actor) = await Client(h); using var owned = client; await WithCsrf(client);
        var guard = h.Factory.Services.GetRequiredService<Clinic.Api.StaffMvc.StaffCreateSubmissions>();
        var token = reason == "missing" ? null : guard.Issue(reason == "actor" ? "someone-else" : actor, reason == "patient" ? Guid.NewGuid() : h.Patient.Id);
        using var response = await client.PostAsync(MvcPath(h) + "/create", MvcCreateBody(submission: token));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode); await AssertNoMutation(h);
    }

    [Fact]
    public async Task MvcHardeningEmptyStatesAndTempDataContainSafeTextOnly()
    {
        using var h = await Seed(); var (client, actor) = await Client(h, permissions: ResultPermissions); using var owned = client; await WithCsrf(client);
        using var list = await client.GetAsync(MvcPath(h) + "?culture=en"); Assert.Contains("No test requests", await list.Content.ReadAsStringAsync());
        using var createPage = await client.GetAsync(MvcPath(h) + "/create?culture=en"); Assert.Contains("No visits are available", await createPage.Content.ReadAsStringAsync());
        var token = Regex.Match(await createPage.Content.ReadAsStringAsync(), "name=\"SubmissionToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        using var created = await client.PostAsync(MvcPath(h) + "/create?culture=en", MvcCreateBody(submission: token));
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        using var scope = h.Factory.Services.CreateScope();
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext { RequestServices = scope.ServiceProvider };
        context.Request.Headers.Cookie = string.Join("; ", created.Headers.GetValues("Set-Cookie").Select(c => c.Split(';', 2)[0]));
        var temp = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Mvc.ViewFeatures.ITempDataProvider>().LoadTempData(context);
        Assert.Single(temp); Assert.Equal("Saved", temp["Notice"]);
        var html = await MvcFollow(client, created, "Saved"); Assert.Contains("No result files yet.", html);
        Assert.DoesNotContain("/review/start", html); Assert.DoesNotContain("/review/complete", html);
        Assert.Contains("data-upload-unavailable", html); Assert.Contains("Upload is unavailable until JavaScript loads", html);
    }

    [Theory]
    [InlineData("session")]
    [InlineData("mfa")]
    [InlineData("role")]
    [InlineData("scope")]
    [InlineData("permission")]
    public async Task MvcHardeningEveryRouteIndependentlyRejectsMissingAccess(string missing)
    {
        using var h = await Seed(); var (writer, _) = await Client(h); using var ow = writer; await WithCsrf(writer);
        var id = await CreateOrder(writer, h);
        var (client, actor) = await Client(h, missing == "role" ? "Receptionist" : "Doctor",
            missing == "scope" ? Guid.NewGuid() : h.Patient.Id, missing == "permission" ? [] : ResultPermissions, missing != "mfa");
        using var owned = client; await WithCsrf(client);
        if (missing == "session") client.DefaultRequestHeaders.Remove("Cookie");
        foreach (var action in new[] { "list", "create-form", "detail", "create", "upload", "start", "complete" })
        {
            using var response = action switch {
                "list" => await client.GetAsync(MvcPath(h)),
                "create-form" => await client.GetAsync(MvcPath(h) + "/create"),
                "detail" => await client.GetAsync(MvcPath(h, id)),
                "create" => await client.PostAsync(MvcPath(h) + "/create", MvcCreateBody()),
                "upload" => await client.PostAsync(MvcPath(h, id) + "/results", ResultBody(await Version(id))),
                _ => await client.PostAsync(MvcPath(h, id) + "/review/" + action, MvcVersion(await Version(id))) };
            Assert.Equal(missing == "session" ? HttpStatusCode.Unauthorized : HttpStatusCode.Forbidden, response.StatusCode);
            Assert.DoesNotContain("Sensitive", await response.Content.ReadAsStringAsync());
        }
        await using var db = database.CreateContext();
        Assert.Empty(await db.AuditEvents.Where(x => x.ActorStaffId == actor && x.PatientId == h.Patient.Id).ToArrayAsync());
        await NoResults(h, id);
    }

    [Fact]
    public async Task MvcHardeningForeignResourcesNeverDiscloseOrAudit()
    {
        using var a = await Seed(); using var b = await Seed();
        var (owner, _) = await Client(b, permissions: ResultPermissions); using var oo = owner; await WithCsrf(owner);
        var id = await CreateOrder(owner, b); using var upload = await UploadResult(owner, b, id, await Version(id));
        Assert.Equal(HttpStatusCode.OK, upload.StatusCode);
        var (client, actor) = await Client(a, permissions: ResultPermissions); using var owned = client; await WithCsrf(client);
        await using var db = database.CreateContext(); var attachment = await db.PatientAttachments.SingleAsync(x => x.PatientId == b.Patient.Id);
        foreach (var action in new[] { "detail", "upload", "start", "complete", "visit", "attachment" })
        {
            var submission = a.Factory.Services.GetRequiredService<Clinic.Api.StaffMvc.StaffCreateSubmissions>().Issue(actor, a.Patient.Id);
            using var response = action switch {
                "detail" => await client.GetAsync(MvcPath(a, id)),
                "upload" => await client.PostAsync(MvcPath(a, id) + "/results", ResultBody(await Version(id))),
                "visit" => await client.PostAsync(MvcPath(a) + "/create", MvcCreateBody(b.Visit.Id, submission: submission)),
                "attachment" => await client.GetAsync($"{a.PatientPath}/attachments/{attachment.Id}/download?ui=staff"),
                _ => await client.PostAsync(MvcPath(a, id) + "/review/" + action, MvcVersion(await Version(id))) };
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            var html = await response.Content.ReadAsStringAsync();
            foreach (var secret in new[] { "Sensitive", "result.pdf", "application/pdf", id.ToString(), b.Visit.Id.ToString(), attachment.Id.ToString() })
                Assert.DoesNotContain(secret, html);
        }
        Assert.Empty(await db.AuditEvents.Where(x => x.PatientId == a.Patient.Id).ToArrayAsync());
    }

    [Theory]
    [InlineData("create")]
    [InlineData("upload")]
    [InlineData("start")]
    [InlineData("complete")]
    public async Task MvcHardeningDuplicatePostsAndRefreshDoNotRepeatMutations(string action)
    {
        using var h = await Seed(); var (client, actor) = await Client(h, permissions: ResultPermissions); using var owned = client; await WithCsrf(client);
        if (action == "create")
        {
            using var page = await client.GetAsync(MvcPath(h) + "/create"); Assert.Equal(HttpStatusCode.OK, page.StatusCode);
            var token = Regex.Match(await page.Content.ReadAsStringAsync(), "name=\"SubmissionToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
            var responses = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => client.PostAsync(MvcPath(h) + "/create?culture=en", MvcCreateBody(submission: token))));
            using var success = Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Redirect);
            using var rejected = Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Conflict);
            Assert.Contains("already submitted or expired", await rejected.Content.ReadAsStringAsync());
            await MvcFollow(client, success, "Saved");
            using var refresh = await client.GetAsync(success.Headers.Location); Assert.Equal(HttpStatusCode.OK, refresh.StatusCode);
            await using var db = database.CreateContext();
            Assert.Single(await db.ClinicalTestRequests.Where(x => x.PatientId == h.Patient.Id).ToArrayAsync());
            Assert.Single(await db.AuditEvents.Where(x => x.PatientId == h.Patient.Id && x.ActionCode == "test-request.create").ToArrayAsync());
            return;
        }
        var id = await CreateOrder(client, h);
        if (action != "upload") { using var upload = await UploadResult(client, h, id, await Version(id)); Assert.Equal(HttpStatusCode.OK, upload.StatusCode); }
        if (action == "complete") { using var start = await ReviewResult(client, h, id, await Version(id)); Assert.Equal(HttpStatusCode.OK, start.StatusCode); }
        var version = await Version(id); var path = MvcPath(h, id) + (action == "upload" ? "/results" : "/review/" + action) + "?culture=en";
        using var first = await client.PostAsync(path, action == "upload" ? ResultBody(version) : MvcVersion(version));
        await MvcFollow(client, first, "Saved");
        var current = await Version(id);
        using var duplicate = await client.PostAsync(path, action == "upload" ? ResultBody(version) : MvcVersion(version));
        var html = await MvcFollow(client, duplicate, "Conflict"); Assert.Equal(current, await Version(id));
        if (action != "complete") Assert.Contains(WebUtility.HtmlEncode(Convert.ToBase64String(current)), html);
        using var refreshPage = await client.GetAsync(first.Headers.Location); Assert.Equal(HttpStatusCode.OK, refreshPage.StatusCode);
        await using var fresh = database.CreateContext();
        var events = await fresh.AuditEvents.Where(x => x.PatientId == h.Patient.Id).ToArrayAsync();
        Assert.Single(events, e => e.ActionCode == "file.upload"); Assert.Single(events, e => e.ActionCode == "test-request.result.upload");
        if (action != "upload") Assert.Single(events, e => e.ActionCode == "test-request.review.start");
        if (action == "complete") Assert.Single(events, e => e.ActionCode == "test-request.review.complete");
    }

    [Theory]
    [InlineData("create")]
    [InlineData("upload")]
    [InlineData("start")]
    [InlineData("complete")]
    public async Task MvcHardeningInvalidCsrfRejectsEveryPost(string action)
    {
        using var seed = await Seed(); var meter = new StaffAttachmentHttpTests.BodyMeter();
        using var h = Instrument(seed, new ResultSaveProbe(), meter: meter);
        var (client, _) = await Client(h, permissions: ResultPermissions); using var owned = client; await WithCsrf(client);
        var id = await CreateOrder(client, h); var version = await Version(id);
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN"); client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", "forged"); meter.Bytes = 0;
        using var response = action == "create" ? await client.PostAsync(MvcPath(h) + "/create", MvcCreateBody())
            : await client.PostAsync(MvcPath(h, id) + (action == "upload" ? "/results" : "/review/" + action),
                action == "upload" ? ResultBody(version) : MvcVersion(version));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        if (action == "upload") Assert.Equal(0, meter.Bytes);
        Assert.Equal(version, await Version(id)); await NoResults(h, id);
    }

    [Theory]
    [InlineData("ar")]
    [InlineData("en")]
    public async Task MvcHardeningUntrustedTextIsEncodedAndMetadataStaysHidden(string culture)
    {
        const string payload = "<img src=x onerror=alert(1)>";
        using var h = await Seed(); var (client, _) = await Client(h, permissions: ResultPermissions); using var owned = client; await WithCsrf(client);
        await using (var db = database.CreateContext()) { (await db.Patients.SingleAsync(x => x.Id == h.Patient.Id)).UpdateContactDetails(payload, "HiddenPhone"); await db.SaveChangesAsync(); }
        using var created = await client.PostAsJsonAsync(h.PatientPath + "/test-requests", Body(name: payload, instructions: "<script>alert(1)</script>"));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("id").GetGuid();
        using var upload = await client.PostAsync(ResultPath(h, id) + "/results", ResultBody(await Version(id), filename: payload + ".pdf"));
        Assert.Equal(HttpStatusCode.OK, upload.StatusCode);
        using var detail = await client.GetAsync(MvcPath(h, id) + "?culture=" + culture); var html = await detail.Content.ReadAsStringAsync();
        Assert.Contains("&lt;img", html); Assert.Contains("&lt;script&gt;", html);
        Assert.DoesNotContain(payload, html); Assert.DoesNotContain("<script>alert", html); Assert.DoesNotContain("HiddenPhone", html);
        var (reader, _) = await Client(h, permissions: ["tests.read"]); using var or = reader;
        using var limited = await reader.GetAsync(MvcPath(h, id) + "?culture=" + culture); html = await limited.Content.ReadAsStringAsync();
        Assert.DoesNotContain(".pdf", html); Assert.DoesNotContain("application/pdf", html); Assert.DoesNotContain("/download", html);
        await using var fresh = database.CreateContext(); var file = await fresh.PatientAttachments.SingleAsync(x => x.PatientId == h.Patient.Id);
        Assert.DoesNotContain(file.Id.ToString(), html); Assert.Contains("(1)", html);
        using var error = await reader.GetAsync(MvcPath(h, Guid.NewGuid()) + "?notice=" + Uri.EscapeDataString(payload));
        Assert.DoesNotContain("onerror", await error.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task MvcHardeningPagingFilteringAndPatientPredicateStayBounded()
    {
        using var h = await Seed(); var (client, _) = await Client(h); using var owned = client; await WithCsrf(client);
        var labs = new[] { await CreateOrder(client, h), await CreateOrder(client, h) };
        await CreateOrder(client, h, category: "Imaging");
        for (var page = 1; page <= 2; page++)
        {
            using var response = await client.GetAsync(MvcPath(h) + $"?culture=en&category=Lab&status=Requested&pageSize=1&page={page}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode); var html = await response.Content.ReadAsStringAsync();
            Assert.Contains("category=Lab", html); Assert.Contains("status=Requested", html); Assert.Contains("pageSize=1", html);
            Assert.Contains("&amp;", html);
            var ids = Regex.Matches(html, "/tests/([0-9a-f-]{36})\\?").Select(m => Guid.Parse(m.Groups[1].Value)).Distinct().ToArray();
            Assert.Single(ids); Assert.Contains(ids[0], labs);
            using var db = database.CreateContext();
            var expected = await db.ClinicalTestRequests.Where(x => x.PatientId == h.Patient.Id && x.Category == ClinicalTestCategory.Lab)
                .OrderByDescending(x => x.RequestedAtUtc).ThenByDescending(x => x.Id).Skip(page - 1).Select(x => x.Id).FirstAsync();
            Assert.Equal(expected, ids[0]);
        }
        foreach (var query in new[] { "status=999", "status=Unknown", "category=999", "pageSize=0", "page=-1" })
        { using var bad = await client.GetAsync(MvcPath(h) + "?" + query); Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode); }
    }

    [Fact]
    public async Task MvcHardeningFormCannotReplaceRoutePatientOrServerFields()
    {
        using var a = await Seed(); using var b = await Seed();
        var (client, actor) = await Client(a, extraClaims: [new Claim("patient_record_id", b.Patient.Id.ToString())]);
        using var owned = client; await WithCsrf(client);
        using var response = await client.PostAsync(MvcPath(a) + "/create", new FormUrlEncodedContent(new Dictionary<string, string> {
            ["SubmissionToken"] = a.Factory.Services.GetRequiredService<Clinic.Api.StaffMvc.StaffCreateSubmissions>().Issue(actor, a.Patient.Id),
            ["patientId"] = b.Patient.Id.ToString(), ["Category"] = "Lab", ["TestName"] = "Route-bound test",
            ["RequestedByDoctorId"] = b.Doctor.Id.ToString(), ["Status"] = "Reviewed", ["RequestedAtUtc"] = "2000-01-01",
            ["ReviewedByDoctorId"] = b.Doctor.Id.ToString(), ["UploadedAtUtc"] = "2000-01-01", ["ReviewedAtUtc"] = "2000-01-01",
            ["StorageKey"] = "forged/path", ["StoredFileId"] = Guid.NewGuid().ToString(), ["CanReview"] = "true", ["role"] = "Doctor" }));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        await using var db = database.CreateContext();
        var request = Assert.Single(await db.ClinicalTestRequests.Where(x => x.PatientId == a.Patient.Id || x.PatientId == b.Patient.Id).ToArrayAsync());
        Assert.Equal(a.Patient.Id, request.PatientId); Assert.Equal(a.Doctor.Id, request.RequestedByDoctorId);
        Assert.Equal(ClinicalTestStatus.Requested, request.Status); Assert.Equal(ServerNow, request.RequestedAtUtc);
        Assert.Null(request.UploadedAtUtc); Assert.Null(request.ReviewedAtUtc); Assert.Null(request.ReviewedByDoctorId);
        var audit = Assert.Single(await db.AuditEvents.Where(x => x.ActorStaffId == actor).ToArrayAsync());
        Assert.Equal(a.Patient.Id, audit.PatientId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MvcHardeningFormCannotReplaceReviewRouteRequest(bool complete)
    {
        using var h = await Seed(); var (client, _) = await Client(h, permissions: ResultPermissions); using var owned = client;
        await WithCsrf(client); var routeId = await CreateOrder(client, h); var postedId = await CreateOrder(client, h);
        foreach (var id in new[] { routeId, postedId })
        {
            using var upload = await UploadResult(client, h, id, await Version(id)); Assert.Equal(HttpStatusCode.OK, upload.StatusCode);
            if (complete) { using var start = await ReviewResult(client, h, id, await Version(id)); Assert.Equal(HttpStatusCode.OK, start.StatusCode); }
        }
        var before = await Version(postedId);
        using var response = await client.PostAsync(MvcPath(h, routeId) + (complete ? "/review/complete" : "/review/start"),
            new FormUrlEncodedContent(new Dictionary<string, string> { ["requestId"] = postedId.ToString(),
                ["patientId"] = Guid.NewGuid().ToString(), ["status"] = "Reviewed", ["reviewedByDoctorId"] = Guid.NewGuid().ToString(),
                ["expectedRowVersion"] = Convert.ToBase64String(before) }));
        await MvcFollow(client, response, "Conflict");
        Assert.Equal(before, await Version(postedId));
    }

    [Fact]
    public async Task MvcHardeningUploadFormCannotReplaceRouteRequestOrPatient()
    {
        using var a = await Seed(); using var b = await Seed();
        var (ownerB, _) = await Client(b, permissions: ResultPermissions); using var ownedB = ownerB; await WithCsrf(ownerB);
        var foreignId = await CreateOrder(ownerB, b);
        // The tampering client legitimately holds both scopes; only route values may win.
        var (client, actor) = await Client(a, permissions: ResultPermissions,
            extraClaims: [new Claim("patient_record_id", b.Patient.Id.ToString())]);
        using var owned = client; await WithCsrf(client);
        var routeId = await CreateOrder(client, a);
        var routeVersion = await Version(routeId);
        var body = ResultBody(routeVersion);
        body.Add(new StringContent(foreignId.ToString()), "requestId");
        body.Add(new StringContent(b.Patient.Id.ToString()), "patientId");
        body.Add(new StringContent("forged/storage-key"), "storageKey");
        body.Add(new StringContent("Reviewed"), "status");
        using var response = await client.PostAsync(MvcPath(a, routeId) + "/results?culture=en", body);
        var html = await MvcFollow(client, response, "Saved");
        Assert.Contains("/attachments/", html); // The uploaded file is listed for the route request.
        await using var db = database.CreateContext();
        Assert.Equal(routeId, Assert.Single(await db.Set<ClinicalTestResultAttachment>()
            .Where(x => x.ClinicalTestRequestId == routeId).ToArrayAsync()).ClinicalTestRequestId);
        Assert.Empty(await db.Set<ClinicalTestResultAttachment>().Where(x => x.ClinicalTestRequestId == foreignId).ToArrayAsync());
        Assert.Equal(1, await db.PatientAttachments.CountAsync(x => x.PatientId == a.Patient.Id));
        Assert.Equal(0, await db.PatientAttachments.CountAsync(x => x.PatientId == b.Patient.Id));
        Assert.Equal(ClinicalTestStatus.Uploaded, (await db.ClinicalTestRequests.SingleAsync(x => x.Id == routeId)).Status);
        Assert.Equal(ClinicalTestStatus.Requested, (await db.ClinicalTestRequests.SingleAsync(x => x.Id == foreignId)).Status);
        var events = await db.AuditEvents.Where(x => x.ActorStaffId == actor && x.ActionCode == "test-request.result.upload").ToArrayAsync();
        var resultEvent = Assert.Single(events);
        Assert.Equal(a.Patient.Id, resultEvent.PatientId);
        Assert.Equal(routeId.ToString("N"), resultEvent.ResourceId);
    }

    [Theory]
    [InlineData("notfound", "ar", HttpStatusCode.NotFound)]
    [InlineData("notfound", "en", HttpStatusCode.NotFound)]
    [InlineData("forbidden", "ar", HttpStatusCode.Forbidden)]
    [InlineData("missing-object", "en", HttpStatusCode.ServiceUnavailable)]
    [InlineData("audit-failure", "ar", HttpStatusCode.InternalServerError)]
    [InlineData("unauthenticated", "en", HttpStatusCode.Unauthorized)]
    public async Task MvcHardeningDownloadUiFailuresAreLocalizedSafeHtml(string scenario, string culture, HttpStatusCode status)
    {
        using var h = await Seed();
        var (writer, _) = await Client(h, permissions: ResultPermissions); using var ownedWriter = writer; await WithCsrf(writer);
        var (client, _) = await Client(h, permissions: scenario == "forbidden" ? ["tests.read"] : ResultPermissions);
        using var owned = client;
        if (scenario != "unauthenticated") await WithCsrf(client);
        var id = await CreateOrder(writer, h);
        using var upload = await UploadResult(writer, h, id, await Version(id)); Assert.Equal(HttpStatusCode.OK, upload.StatusCode);
        Guid attachmentId; string storageKey;
        await using (var db = database.CreateContext())
        {
            var row = await db.PatientAttachments.Include(x => x.File).SingleAsync(x => x.PatientId == h.Patient.Id);
            attachmentId = row.Id; storageKey = row.File.StorageKey;
        }
        HttpResponseMessage response;
        if (scenario == "notfound") attachmentId = Guid.NewGuid();
        else if (scenario == "missing-object")
        {
            using var scope = h.Factory.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<Clinic.Application.Storage.IFileStorage>().DeleteAsync(storageKey);
        }
        else if (scenario == "audit-failure") h.Failure.Enabled = true;
        else if (scenario == "unauthenticated")
        {
            client.DefaultRequestHeaders.Remove("Cookie");
            client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        }
        using (response = await client.GetAsync(
            $"/api/staff/patients/{h.Patient.Id}/attachments/{attachmentId}/download?ui=staff&culture={culture}"))
        {
            Assert.Equal(status, response.StatusCode);
            Assert.Equal("text/html", response.Content.Headers.ContentType!.MediaType);
            // The renderer HTML-encodes non-ASCII, so localized Arabic arrives as entities.
            var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
            Assert.True(response.Headers.CacheControl!.NoStore);
            Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
            Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
            Assert.Contains("frame-ancestors 'none'", response.Headers.GetValues("Content-Security-Policy").Single());
            Assert.DoesNotContain(culture == "ar" ? "dir=\"ltr\"" : "dir=\"rtl\"", html);
            Assert.DoesNotContain("%PDF", html); Assert.DoesNotContain("Sensitive", html);
            Assert.DoesNotContain("result.pdf", html); Assert.DoesNotContain("storageKey", html);
            Assert.DoesNotContain(storageKey, html); Assert.DoesNotContain("Exception", html);
            Assert.DoesNotContain(attachmentId.ToString(), html);
            var english = culture == "en";
            var expected = (scenario, english) switch
            {
                ("forbidden", true) => "do not have access", ("forbidden", false) => "ليس لديك صلاحية",
                ("notfound", true) => "unavailable", ("notfound", false) => "المورد المطلوب",
                ("unauthenticated", true) => "MFA-verified staff session", ("unauthenticated", false) => "سجّل الدخول",
                (_, true) => "Unable to complete the operation", (_, false) => "تعذر إكمال العملية",
            };
            Assert.Contains(expected, html);
        }
        if (scenario != "audit-failure") return;
        await using var fresh = database.CreateContext();
        Assert.Empty(await fresh.AuditEvents.Where(x => x.PatientId == h.Patient.Id && x.ActionCode == "file.download").ToArrayAsync());
    }
}
