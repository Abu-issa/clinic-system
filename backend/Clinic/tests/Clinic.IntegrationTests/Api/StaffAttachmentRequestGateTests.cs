using System.Net;
using System.Security.Claims;
using Clinic.Application.Storage;
using Clinic.Domain.Entities;
using Clinic.Domain.Enums;
using Clinic.Infrastructure.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Clinic.IntegrationTests.Api;

public sealed partial class StaffAttachmentHttpTests
{
    private sealed class BodyMeter : IStartupFilter
    {
        public long Bytes;
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (http, continuation) =>
            {
                http.Request.Body = new MeterStream(http.Request.Body, this);
                await continuation();
            });
            next(app);
        };
        private sealed class MeterStream(Stream inner, BodyMeter meter) : Stream
        {
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
            { var read = await inner.ReadAsync(buffer, ct); meter.Bytes += read; return read; }
            public override Task<int> ReadAsync(byte[] b, int o, int c, CancellationToken ct) => ReadAsync(b.AsMemory(o, c), ct).AsTask();
            public override int Read(byte[] b, int o, int c) { var read = inner.Read(b, o, c); meter.Bytes += read; return read; }
            public override void Flush() => throw new NotSupportedException();
            public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
            public override void SetLength(long l) => throw new NotSupportedException();
            public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
        }
    }

    [Theory]
    [InlineData("missing-csrf")]
    [InlineData("invalid-csrf")]
    [InlineData("foreign-visit")]
    public async Task RejectedUploadReadsNoRequestBytesAndLeavesNoRowsOrObjects(string rejection)
    {
        using var seed = await Seed();
        var meter = new BodyMeter();
        using var factory = new BookingApiFactory(database.ConnectionString).WithWebHostBuilder(b =>
            b.ConfigureTestServices(s => s.AddSingleton<IStartupFilter>(meter)));
        using var h = new Harness(factory, seed.Failure, seed.Patient, seed.Doctor, seed.Visit);
        var (client, actor) = await Client(h);
        using var owned = client;
        var path = $"{h.PatientPath}/attachments";
        if (rejection != "missing-csrf") await WithCsrf(client);
        if (rejection == "invalid-csrf")
        {
            client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
            client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", "invalid");
        }
        if (rejection == "foreign-visit")
        {
            var other = new Patient("Other", "Other");
            var visit = new Visit(other.Id, h.Doctor.Id, null, DateTimeOffset.UtcNow, "seed", DateTimeOffset.UtcNow);
            await using var db = database.CreateContext();
            db.AddRange(other, visit);
            await db.SaveChangesAsync();
            path = $"{h.PatientPath}/visits/{visit.Id}/attachments";
        }
        using var response = await client.PostAsync(path, UploadBody(PdfBytes));
        Assert.Equal(rejection == "foreign-visit" ? HttpStatusCode.NotFound : HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, meter.Bytes);
        AssertNoObjects(factory.Services);
        await using var fresh = database.CreateContext();
        Assert.False(await fresh.Set<StoredFile>().AnyAsync(x => x.CreatedByStaffId == actor));
        Assert.False(await fresh.PatientAttachments.AnyAsync(x => x.PatientId == h.Patient.Id));
        Assert.False(await fresh.AuditEvents.AnyAsync(x => x.PatientId == h.Patient.Id && x.ActionCode == "file.upload"));
    }

    private sealed class UnknownLengthMultipart : MultipartFormDataContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }

    [Fact]
    public async Task ConfiguredRequestCapStopsUnknownLengthMultipartBeforeStorage()
    {
        using var seed = await Seed();
        var meter = new BodyMeter();
        using var factory = new BookingApiFactory(database.ConnectionString).WithWebHostBuilder(b =>
        {
            b.UseSetting("Attachments:MaxFileSizeBytes", "64");
            b.ConfigureTestServices(s => s.AddSingleton<IStartupFilter>(meter));
        });
        using var h = new Harness(factory, seed.Failure, seed.Patient, seed.Doctor, seed.Visit);
        var (client, actor) = await Client(h);
        using var owned = client;
        await WithCsrf(client);
        using var body = new UnknownLengthMultipart();
        var file = new ByteArrayContent("%PDF-"u8.ToArray().Concat(new byte[200000]).ToArray());
        file.Headers.ContentType = new("application/pdf");
        body.Add(file, "file", "large.pdf");
        using var response = await client.PostAsync($"{h.PatientPath}/attachments", body);
        // MVC converts the bounded request-stream exception into a form-binding 400.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.InRange(meter.Bytes, 1, 64 + 65536 + 1);
        AssertNoObjects(factory.Services);
        await using var db = database.CreateContext();
        Assert.False(await db.Set<StoredFile>().AnyAsync(x => x.CreatedByStaffId == actor));
        Assert.False(await db.PatientAttachments.AnyAsync(x => x.PatientId == h.Patient.Id));
        Assert.False(await db.AuditEvents.AnyAsync(x => x.PatientId == h.Patient.Id && x.ActionCode == "file.upload"));
    }

    [Theory]
    [InlineData("wrong")]
    [InlineData("appointment")]
    [InlineData("schedule")]
    [InlineData("client")]
    public async Task OtherDoctorSignalsNeverOverrideLiveVisitAuthority(string signal)
    {
        using var h = await Seed();
        var otherDoctor = new Doctor("Other doctor");
        await using (var db = database.CreateContext()) { db.Add(otherDoctor); await db.SaveChangesAsync(); }
        var claims = signal == "appointment" ? new[] { new Claim("appointment_doctor_id", h.Doctor.Id.ToString()) }
            : signal == "schedule" ? new[] { new Claim("schedule_doctor_id", h.Doctor.Id.ToString()) } : [];
        var (client, actor) = await Client(h, associateDoctor: true, extraClaims: claims);
        using var owned = client;
        await WithCsrf(client);
        // Change association after authentication without rotating the cookie.
        await using (var db = database.CreateContext())
        {
            var user = await db.Users.SingleAsync(x => x.Id == actor);
            user.AssociatedDoctorId = otherDoctor.Id;
            await db.SaveChangesAsync();
        }
        var targetVisit = h.Visit.Id;
        if (signal == "appointment")
        {
            var now = DateTimeOffset.UtcNow;
            var appointment = new Appointment(h.Patient.Id, otherDoctor.Id, now.AddDays(1), now.AddDays(1).AddMinutes(30), AppointmentType.Consultation);
            var visit = new Visit(h.Patient.Id, h.Doctor.Id, appointment.Id, now, "seed", now);
            await using var db = database.CreateContext();
            db.AddRange(appointment, visit);
            await db.SaveChangesAsync();
            targetVisit = visit.Id;
        }
        using var body = UploadBody(PdfBytes);
        body.Add(new StringContent(h.Doctor.Id.ToString()), "doctorId");
        body.Add(new StringContent(h.Doctor.Id.ToString()), "associatedDoctorId");
        using var response = await client.PostAsync($"{h.PatientPath}/visits/{targetVisit}/attachments?doctorId={h.Doctor.Id}", body);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        AssertNoObjects(h.Factory.Services);
        await using var fresh = database.CreateContext();
        Assert.False(await fresh.PatientAttachments.AnyAsync(x => x.PatientId == h.Patient.Id));
        Assert.False(await fresh.AuditEvents.AnyAsync(x => x.PatientId == h.Patient.Id && x.ActionCode == "file.upload"));
    }

    [Fact]
    public async Task AssistantVisitUploadUsesExistingScopePermissionAndMfaWithoutDoctorAssociation()
    {
        using var h = await Seed();
        var (client, _) = await Client(h, role: "DoctorAssistant");
        using var owned = client;
        await WithCsrf(client);
        using var response = await client.PostAsync($"{h.PatientPath}/visits/{h.Visit.Id}/attachments", UploadBody(PdfBytes));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }
}
