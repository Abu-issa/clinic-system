using System.Net;
using System.Net.Http.Json;
using Clinic.Api.Appointments;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Clinic.IntegrationTests.Api;

public sealed class AppointmentReschedulingAccessTests(ErrorHandlingApiFactory factory)
    : IClassFixture<ErrorHandlingApiFactory>
{
    [Theory]
    [InlineData("anonymous", 401)]
    [InlineData("role", 403)]
    [InlineData("mfa", 403)]
    [InlineData("permission", 403)]
    [InlineData("identity", 403)]
    [InlineData("scope", 403)]
    public async Task ReadAndPost_RequireReschedulingAccess(string scenario, int status)
    {
        var doctorId = Guid.NewGuid();
        using var client = scenario == "anonymous"
            ? factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false
            })
            : AppointmentReschedulingHttpTests.CreateClient(factory,
                scenario == "scope" ? Guid.NewGuid() : doctorId,
                role: scenario == "role" ? "Patient" : "Doctor",
                mfa: scenario != "mfa",
                permission: scenario == "permission" ? "appointments.cancel" : "appointments.reschedule",
                actor: scenario == "identity" ? null : "staff-user");
        var url = AppointmentReschedulingHttpTests.Url(doctorId, Guid.NewGuid());
        using var read = await client.GetAsync(url);
        Assert.Equal((HttpStatusCode)status, read.StatusCode);
        var start = DateTimeOffset.UtcNow.AddDays(7);
        using var post = await client.PostAsJsonAsync(url,
            new RescheduleAppointmentBody(start, start.AddMinutes(30), "Move", new byte[8]));
        Assert.Equal((HttpStatusCode)status, post.StatusCode);
        Assert.Null(read.Headers.Location);
        Assert.Null(post.Headers.Location);
    }
}
