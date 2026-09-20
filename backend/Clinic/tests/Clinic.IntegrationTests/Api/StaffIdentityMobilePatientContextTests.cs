using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Clinic.Application.Patients;
using Clinic.Application.Audit;
using Clinic.Domain.Entities;
using Clinic.Domain.Enums;
using Clinic.Infrastructure.Authentication;
using Clinic.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Clinic.IntegrationTests.Api;

public sealed partial class StaffIdentityHttpTests
{
    private const string ContextRoute = "/api/mobile/staff/patients/search";
    private async Task<Patient[]> ContextPatients(string role = "Doctor", bool permission = true, bool scope = true)
    {
        using var services = _factory.Services.CreateScope();
        var db = services.ServiceProvider.GetRequiredService<ClinicDbContext>();
        var patients = Enumerable.Range(0, 4).Select(i => new Patient($"CTX-MRN-{i}", $"Context Patient {i}",
            "private-phone", new DateOnly(1990, 2, 3), $"PAPER-CTX-{i}", "private-cover")).ToArray();
        db.AddRange(patients);
        var profile = new PatientMedicalProfile(patients[0].Id, _id, _clock.Now);
        profile.RecordAllergy("private-substance", "private-reaction", AllergySeverity.Severe,
            MedicalRecordSource.Staff, _id, _clock.Now);
        profile.RecordChronicCondition("private-condition", "private-notes", MedicalRecordSource.Staff, _id, _clock.Now);
        db.Add(profile);
        await db.SaveChangesAsync();
        await services.ServiceProvider.GetRequiredService<StaffAdministration>().ChangeAsync(_id, "set-grants",
            approvedRoles: [role], permissions: permission ? ["patients.clinical.read"] : [],
            patientScopes: scope ? patients.Take(3).Select(p => p.Id).ToArray() : []);
        return patients;
    }

    [Theory]
    [InlineData("Doctor")]
    [InlineData("DoctorAssistant")]
    public async Task MobileContextSearchUsesClinicalPolicyAndMinimalAuditedProjection(string role)
    {
        var patients = await ContextPatients(role);
        using var web = Client();
        var enrollment = await Enroll(web);
        using var mobile = Client(false);
        await MobileComplete(mobile, enrollment.Key);
        using var response = await mobile.PostAsJsonAsync(ContextRoute, new { searchTerm = " Context Patient ", pageSize = 2 });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var json = await Json(response);
        Assert.True(json.GetProperty("hasMore").GetBoolean());
        var items = json.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(2, items.Length);
        Assert.Equal(patients[0].Id, items[0].GetProperty("patientId").GetGuid());
        Assert.Equal(new[] { "allergyStatus", "dateOfBirth", "fullName", "medicalRecordNumber", "patientId" },
            items[0].EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal(2, items[0].GetProperty("allergyStatus").GetInt32());
        Assert.Equal(0, items[1].GetProperty("allergyStatus").GetInt32());
        Assert.Equal("1990-02-03", items[0].GetProperty("dateOfBirth").GetString());
        Assert.DoesNotContain("private-", await Body(response));
        await using var db = _database.CreateContext();
        var events = await db.AuditEvents.Where(x => x.ActionCode == "patient.context.read").ToListAsync();
        Assert.Equal(2, events.Count);
        Assert.All(events, e => {
            Assert.Equal(_id, e.ActorStaffId);
            Assert.Equal("patient", e.ResourceType);
            Assert.Equal(e.PatientId!.Value.ToString("N"), e.ResourceId);
            Assert.Empty(e.Metadata);
            Assert.Equal(AuditOutcome.Succeeded, e.Outcome);
        });
        Assert.DoesNotContain(events, e => e.PatientId == patients[2].Id || e.PatientId == patients[3].Id);
        Assert.DoesNotContain(_logs.Messages, line => line.Contains("Context Patient") || line.Contains("CTX-MRN"));
    }

    [Theory]
    [InlineData("Context Patient 1")]
    [InlineData("CTX-MRN-1")]
    [InlineData("PAPER-CTX-1")]
    public async Task MobileContextSearchMatchesNameMrnAndLegacy(string term)
    {
        var patients = await ContextPatients();
        using var web = Client(); var enrolled = await Enroll(web);
        using var mobile = Client(false); await MobileComplete(mobile, enrolled.Key);
        var page = await (await mobile.PostAsJsonAsync(ContextRoute, new { searchTerm = term })).Content.ReadFromJsonAsync<PatientContextPage>();
        Assert.Equal(patients[1].Id, Assert.Single(page!.Items).PatientId);
    }

    [Fact]
    public async Task MobileContextPaginationAndLiteralSearchCannotExposeOtherPatients()
    {
        var patients = await ContextPatients();
        using var web = Client(); var enrolled = await Enroll(web);
        using var mobile = Client(false); await MobileComplete(mobile, enrolled.Key);
        var seen = new List<Guid>();
        for (var page = 1; page <= 4; page++)
        {
            var result = await (await mobile.PostAsJsonAsync(ContextRoute, new { searchTerm = "Context", page, pageSize = 1 }))
                .Content.ReadFromJsonAsync<PatientContextPage>();
            Assert.Equal(page < 3, result!.HasMore);
            seen.AddRange(result.Items.Select(x => x.PatientId));
        }
        Assert.Equal(patients.Take(3).Select(p => p.Id), seen);
        mobile.DefaultRequestHeaders.Add("X-Patient-Id", patients[3].Id.ToString());
        foreach (var term in new[] { "CTX-MRN-3", "%%", "__", "' OR 1=1 --" })
        {
            var response = await mobile.PostAsJsonAsync(ContextRoute, new { searchTerm = term, patientId = patients[3].Id, staffId = _id });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Empty((await response.Content.ReadFromJsonAsync<PatientContextPage>())!.Items);
        }
    }

    [Theory]
    [InlineData("", 1, 10)]
    [InlineData("a", 1, 10)]
    [InlineData("Context\nPatient", 1, 10)]
    [InlineData("Context", 0, 10)]
    [InlineData("Context", 101, 10)]
    [InlineData("Context", 1, 0)]
    [InlineData("Context", 1, 21)]
    public async Task MobileContextBoundsRejectBroadSearch(string term, int page, int pageSize)
    {
        await ContextPatients();
        using var web = Client(); var enrolled = await Enroll(web);
        using var mobile = Client(false); await MobileComplete(mobile, enrolled.Key);
        Assert.Equal(HttpStatusCode.BadRequest, (await mobile.PostAsJsonAsync(ContextRoute, new { searchTerm = term, page, pageSize })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await mobile.PostAsJsonAsync(ContextRoute, new { searchTerm = new string('x', 101) })).StatusCode);
        await using var db = _database.CreateContext();
        Assert.False(await db.AuditEvents.AnyAsync(x => x.ActionCode == "patient.context.read"));
    }

    [Theory]
    [InlineData("Receptionist", true, true)]
    [InlineData("Doctor", false, true)]
    [InlineData("DoctorAssistant", true, false)]
    public async Task MobileContextDeniesUnapprovedRolePermissionOrScope(string role, bool permission, bool scope)
    {
        await ContextPatients(role, permission, scope);
        using var web = Client(); var enrolled = await Enroll(web);
        using var mobile = Client(false); await MobileComplete(mobile, enrolled.Key);
        mobile.DefaultRequestHeaders.Add("X-Role", "Doctor");
        mobile.DefaultRequestHeaders.Add("X-Permission", "patients.clinical.read");
        Assert.Equal(HttpStatusCode.Forbidden, (await mobile.PostAsJsonAsync(ContextRoute, new { searchTerm = "Context" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await web.PostAsJsonAsync(ContextRoute, new { searchTerm = "Context" })).StatusCode);
        using var anonymous = Client(false);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync(ContextRoute, new { searchTerm = "Context" })).StatusCode);
        await using var db = _database.CreateContext();
        Assert.False(await db.AuditEvents.AnyAsync(x => x.ActionCode == "patient.context.read"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MobileContextRechecksPersistedGrantRemovalWithoutStampChange(bool permission)
    {
        var patients = await ContextPatients();
        using var web = Client(); var enrolled = await Enroll(web);
        using var mobile = Client(false); await MobileComplete(mobile, enrolled.Key);
        using (var scope = _factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<StaffUser>>();
            var user = (await users.FindByIdAsync(_id))!;
            var stamp = user.SecurityStamp;
            Assert.True((await users.RemoveClaimAsync(user, permission ? new Claim("permission", "patients.clinical.read") :
                new Claim("patient_record_id", patients[0].Id.ToString()))).Succeeded);
            Assert.Equal(stamp, user.SecurityStamp);
        }
        var response = await mobile.PostAsJsonAsync(ContextRoute, new { searchTerm = "CTX-MRN-0" });
        if (permission) Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        else Assert.Empty((await response.Content.ReadFromJsonAsync<PatientContextPage>())!.Items);
    }

    private sealed class ContextAuditFailure : IAccessAuditWriter
    {
        public Task WriteAsync(AuditAppendRequest request, CancellationToken ct = default) =>
            throw new InvalidOperationException("Synthetic audit outage");
    }

    [Fact]
    public async Task MobileContextAuditFailurePreventsDisclosure()
    {
        await ContextPatients();
        using var web = Client(); var enrolled = await Enroll(web);
        using var mobile = Client(false); var token = await MobileComplete(mobile, enrolled.Key);
        using var failureHost = _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddScoped<IAccessAuditWriter, ContextAuditFailure>()));
        using var failing = failureHost.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        failing.DefaultRequestHeaders.Authorization = new("Bearer", token);
        var response = await failing.PostAsJsonAsync(ContextRoute, new { searchTerm = "Context" });
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.DoesNotContain("Context Patient", await Body(response));
        Assert.DoesNotContain("CTX-MRN", await Body(response));
    }
}
