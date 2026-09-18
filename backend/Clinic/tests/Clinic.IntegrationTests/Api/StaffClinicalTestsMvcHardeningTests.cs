using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Clinic.Domain.Entities;
using Clinic.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Clinic.IntegrationTests.Api;

public sealed partial class StaffClinicalTestsHttpTests
{
    [Fact]
    public async Task MvcHardeningFormCannotReplaceRoutePatientOrServerFields()
    {
        using var a = await Seed(); using var b = await Seed();
        var (client, actor) = await Client(a, extraClaims: [new Claim("patient_record_id", b.Patient.Id.ToString())]);
        using var owned = client; await WithCsrf(client);
        using var response = await client.PostAsync(MvcPath(a) + "/create", new FormUrlEncodedContent(new Dictionary<string, string> {
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
        Assert.Empty(await fresh.AuditEvents.Where(x => x.ActionCode == "file.download").ToArrayAsync());
    }
}
