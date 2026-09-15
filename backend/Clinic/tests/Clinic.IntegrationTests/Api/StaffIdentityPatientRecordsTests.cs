using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Clinic.Application.Patients;
using Clinic.Domain.Entities;
using Clinic.Infrastructure.Authentication;
using Clinic.Infrastructure.Persistence;
using Clinic.IntegrationTests.Patients;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Clinic.IntegrationTests.Api;

// Reuses the real password/TOTP/CSRF setup from StaffIdentityHttpTests; no generated tickets.
public sealed partial class StaffIdentityHttpTests
{
    [Theory]
    [InlineData("scope")]
    [InlineData("permission")]
    [InlineData("role")]
    public async Task Review_RemovedPersistedPatientScopeInvalidatesCapturedCookieWithoutStampChange(string grant)
    {
        var id = await PatientGrants();
        using var client = Client();
        await Enroll(client);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/staff/patients/{id}")).StatusCode);
        using (var scope = _factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<StaffUser>>();
            var user = (await users.FindByIdAsync(_id))!;
            var stamp = user.SecurityStamp;
            if (grant == "role")
            {
                Assert.True((await users.AddToRoleAsync(user, "DoctorAssistant")).Succeeded);
                Assert.True((await users.RemoveFromRoleAsync(user, "Doctor")).Succeeded);
            }
            else
                Assert.True((await users.RemoveClaimAsync(user, grant == "scope"
                    ? new System.Security.Claims.Claim("patient_record_id", id.ToString())
                    : new System.Security.Claims.Claim("permission", "patients.admin.read"))).Succeeded);
            Assert.Equal(stamp, user.SecurityStamp);
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync($"/api/staff/patients/{id}")).StatusCode);
    }
    [Theory]
    [InlineData("bloodType")]
    [InlineData("reaction")]
    public async Task Review_MissingClinicalFieldsMustNotClearRecordedValues(string field)
    {
        var id = await PatientGrants();
        using var client = Client();
        await Enroll(client);
        var first = await client.PutAsJsonAsync($"/api/staff/patients/{id}/medical-profile",
            PatientRecordsTests.Snapshot() with { BloodType = Clinic.Domain.Enums.BloodType.APositive });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var details = (await first.Content.ReadFromJsonAsync<MedicalProfileDetails>())!;
        var partial = JsonSerializer.SerializeToNode(PatientRecordsTests.Retain(details), new JsonSerializerOptions(JsonSerializerDefaults.Web))!.AsObject();
        if (field == "bloodType") partial.Remove(field);
        else partial["allergies"]![0]!.AsObject().Remove(field);
        var response = await client.PutAsJsonAsync($"/api/staff/patients/{id}/medical-profile", partial);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var current = (await client.GetFromJsonAsync<MedicalProfileDetails>($"/api/staff/patients/{id}/medical-profile"))!;
        Assert.Equal(details.BloodType, current.BloodType);
        Assert.Equal(details.RowVersion, current.RowVersion);
    }
    [Fact]
    public async Task Review_ConcurrentHttpMrnConflictDoesNotLeakMedicalValuesToLogs()
    {
        await PatientGrants("Receptionist");
        using var client = Client();
        await Enroll(client);
        var mrn = Guid.NewGuid().ToString("N");
        var body = new { fullName = "Synthetic-private-name-" + mrn, phoneNumber = "synthetic-shared", medicalRecordNumber = mrn };
        var responses = await Task.WhenAll(client.PostAsJsonAsync("/api/staff/patients", body),
            client.PostAsJsonAsync("/api/staff/patients", body));
        Assert.Single(responses, x => x.StatusCode == HttpStatusCode.Created);
        var conflict = Assert.Single(responses, x => x.StatusCode == HttpStatusCode.Conflict);
        Assert.Equal("medical_record_number_already_exists", (await Json(conflict)).GetProperty("code").GetString());
        Assert.DoesNotContain(mrn, await Body(conflict));
        Assert.DoesNotContain(_logs.Messages, x => x.Contains(mrn, StringComparison.Ordinal));
        foreach (var response in responses) response.Dispose();
    }
    private static readonly string[] PatientPermissions =
        ["patients.admin.read", "patients.admin.write", "patients.clinical.read", "patients.clinical.write"];

    private async Task<Guid> PatientGrants(string role = "Doctor", bool permission = true, bool scope = true)
    {
        using var services = _factory.Services.CreateScope();
        var db = services.ServiceProvider.GetRequiredService<ClinicDbContext>();
        var patient = new Patient("Synthetic record patient", "shared");
        db.Add(patient); await db.SaveChangesAsync();
        await services.ServiceProvider.GetRequiredService<StaffAdministration>().ChangeAsync(_id, "set-grants",
            approvedRoles: [role], permissions: permission ? PatientPermissions : [], scopes: [_doctor],
            patientScopes: scope ? [patient.Id] : []);
        return patient.Id;
    }

    [Theory]
    [InlineData("Doctor", true, true, 200, 200)]
    [InlineData("Receptionist", true, true, 200, 403)]
    [InlineData("DoctorAssistant", true, true, 403, 200)]
    [InlineData("Doctor", false, true, 403, 403)]
    [InlineData("Doctor", true, false, 403, 403)]
    public async Task PatientAccessRequiresRolePermissionAndExactPatientScope(string role, bool permission, bool scope, int admin, int clinical)
    {
        var patient = await PatientGrants(role, permission, scope);
        using var client = Client();
        await Enroll(client);
        var response = await client.GetAsync($"/api/staff/patients/{patient}");
        Assert.Equal(admin, (int)response.StatusCode); Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal(clinical, (int)(await client.GetAsync($"/api/staff/patients/{patient}/medical-profile")).StatusCode);
        client.DefaultRequestHeaders.Add("X-Role", "Doctor");
        client.DefaultRequestHeaders.Add("X-Permission", "patients.clinical.write");
        client.DefaultRequestHeaders.Add("X-Patient-Id", patient.ToString());
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/staff/patients/{Guid.NewGuid()}/medical-profile")).StatusCode);
        if (role != "Doctor")
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync($"/api/staff/patients/{patient}/medical-profile", PatientRecordsTests.Snapshot())).StatusCode);
    }

    [Fact]
    public async Task PatientCreationHasNoAccountAndRejectsSecurityOverposting()
    {
        await PatientGrants("Receptionist");
        using var client = Client();
        await Enroll(client);
        var forged = new {
            fullName = "Synthetic created", phoneNumber = "shared", medicalRecordNumber = "SYN-NEW",
            legacyPaperFileNumber = "PAPER-42", id = Guid.NewGuid(), roles = new[] { "Doctor" },
            permission = "patients.clinical.write", staffId = _id, rowVersion = Convert.ToBase64String(new byte[8])
        };
        var response = await client.PostAsJsonAsync("/api/staff/patients", forged);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode); Assert.True(response.Headers.CacheControl?.NoStore);
        var body = await Json(response);
        var id = body.GetProperty("patientId").GetGuid();
        Assert.NotEqual(forged.id, id);
        Assert.Equal("PAPER-42", body.GetProperty("legacyPaperFileNumber").GetString());
        Assert.False(body.TryGetProperty("roles", out _));
        // Creation does not grant access to the new resource or expand any scope.
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/staff/patients/{id}")).StatusCode);
        using var services = _factory.Services.CreateScope();
        var db = services.ServiceProvider.GetRequiredService<ClinicDbContext>();
        Assert.Equal(1, await db.Users.CountAsync());
        Assert.Equal("SYN-NEW", (await db.Patients.SingleAsync(x => x.Id == id)).MedicalRecordNumber);
    }

    [Fact]
    public async Task PatientCsrfRestrictedIsolationAndClinicalConcurrencyUseRealIdentity()
    {
        var id = await PatientGrants();
        using var client = Client();
        await Login(client);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync($"/api/staff/patients/{id}")).StatusCode);
        // Fresh enrollment completes and replaces the pending challenge.
        await Enroll(client);
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"/api/staff/patients/{id}/medical-profile", PatientRecordsTests.Snapshot())).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/staff/patients", new { fullName = "S", phoneNumber = "P", medicalRecordNumber = "MRN" })).StatusCode);
        await Csrf(client);
        var first = await client.PutAsJsonAsync($"/api/staff/patients/{id}/medical-profile", PatientRecordsTests.Snapshot());
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var detail = await first.Content.ReadFromJsonAsync<MedicalProfileDetails>();
        var update = PatientRecordsTests.ReplaceAll(detail!);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"/api/staff/patients/{id}/medical-profile", update)).StatusCode);
        var stale = await client.PutAsJsonAsync($"/api/staff/patients/{id}/medical-profile", update);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("profile_changed", (await Json(stale)).GetProperty("code").GetString());
        var profileBody = await Body(await client.GetAsync($"/api/staff/patients/{id}/medical-profile"));
        Assert.DoesNotContain(_id, profileBody);
        Assert.DoesNotContain("verifiedByStaffId", profileBody);
        Assert.DoesNotContain("securityStamp", profileBody);
    }

    [Fact]
    public async Task PatientGrantChangeRevokesCapturedRealSessionAndDoesNotExpandProvisioning()
    {
        using var client = Client();
        await Enroll(client);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/staff/patients",
            new { fullName = "Synthetic", phoneNumber = "shared", medicalRecordNumber = "SYN" })).StatusCode);
        var id = await PatientGrants();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync($"/api/staff/patients/{id}")).StatusCode);
        await Login(client);
        using (var services = _factory.Services.CreateScope())
        {
            var users = services.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<StaffUser>>();
            var key = await users.GetAuthenticatorKeyAsync((await users.FindByIdAsync(_id))!);
            var second = await client.PostAsJsonAsync("/api/staff/auth/totp", new { code = Totp(key!) });
            Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        }
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/staff/patients/{id}")).StatusCode);
    }
}
