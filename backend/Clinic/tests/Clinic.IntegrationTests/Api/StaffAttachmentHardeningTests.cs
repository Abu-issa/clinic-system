using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;
using Clinic.Application.Storage;
using Clinic.Domain.Entities;
using Clinic.Infrastructure.Persistence;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Clinic.IntegrationTests.Api;

public sealed partial class StaffAttachmentHttpTests
{
    private sealed class FailInsert(string table) : DbCommandInterceptor
    {
        public int Attempts { get; private set; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains($"INSERT INTO [{table}]", StringComparison.Ordinal))
            {
                Attempts++;
                command.CommandText = "THROW 51000, 'Synthetic insert failure', 1;";
            }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class ObserveUploadSave : SaveChangesInterceptor
    {
        public int Saves { get; private set; }
        public Guid FileId { get; private set; }
        public Guid AttachmentId { get; private set; }
        public string? StorageKey { get; private set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            var db = eventData.Context!;
            var file = db.ChangeTracker.Entries<StoredFile>().SingleOrDefault(x => x.State == EntityState.Added);
            if (file is not null)
            {
                Saves++;
                FileId = file.Entity.Id;
                StorageKey = file.Entity.StorageKey;
                AttachmentId = Assert.Single(db.ChangeTracker.Entries<PatientAttachment>(), x => x.State == EntityState.Added).Entity.Id;
                Assert.Single(db.ChangeTracker.Entries<AuditEvent>(), x => x.State == EntityState.Added && x.Entity.ActionCode == "file.upload");
            }
            return ValueTask.FromResult(result);
        }
    }

    [Theory]
    [InlineData("StoredFiles")]
    [InlineData("PatientAttachments")]
    [InlineData("AuditEvents")]
    public async Task EachSqlInsertFailureRollsBackAllRowsAndDeletesPromotedBytes(string table)
    {
        using var seed = await Seed();
        var failure = new FailInsert(table);
        var saves = new ObserveUploadSave();
        using var factory = new BookingApiFactory(database.ConnectionString).WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => services.AddDbContext<ClinicDbContext>(o =>
                o.UseSqlServer(database.ConnectionString, sql => sql.MaxBatchSize(1)).AddInterceptors(failure, saves))));
        using var h = new Harness(factory, seed.Failure, seed.Patient, seed.Doctor, seed.Visit);
        var (client, actor) = await Client(h);
        using var owned = client;
        await WithCsrf(client);
        using var response = await client.PostAsync($"{h.PatientPath}/attachments", UploadBody(PdfBytes));
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(1, failure.Attempts);
        Assert.Equal(1, saves.Saves);
        Assert.NotEqual(Guid.Empty, saves.FileId);
        await using var fresh = database.CreateContext();
        Assert.False(await fresh.Set<StoredFile>().AnyAsync(x => x.Id == saves.FileId || x.CreatedByStaffId == actor));
        Assert.False(await fresh.PatientAttachments.AnyAsync(x => x.Id == saves.AttachmentId || x.PatientId == h.Patient.Id));
        Assert.False(await fresh.AuditEvents.AnyAsync(x => x.ActionCode == "file.upload" && x.PatientId == h.Patient.Id));
        using var scope = factory.Services.CreateScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<IFileStorage>().ExistsAsync(saves.StorageKey!));
        AssertNoObjects(factory.Services);
    }

    private static void AssertNoObjects(IServiceProvider services)
    {
        var root = services.GetRequiredService<IOptions<FileStorageOptions>>().Value.LocalRoot;
        Assert.False(Directory.Exists(root) && Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Any());
    }

    [Theory]
    [InlineData("permission", "attachments.read", false)]
    [InlineData("permission", "attachments.write", true)]
    [InlineData("patient_record_id", null, false)]
    [InlineData("patient_record_id", null, true)]
    public async Task ExplicitGrantRemovalImmediatelyRejectsExistingCookie(string claimType, string? claimValue, bool upload)
    {
        using var h = await Seed();
        var (client, actor) = await Client(h);
        using var owned = client;
        await WithCsrf(client);
        await using (var db = database.CreateContext())
        {
            var claim = await db.UserClaims.SingleAsync(x => x.UserId == actor && x.ClaimType == claimType &&
                (claimValue == null || x.ClaimValue == claimValue));
            db.UserClaims.Remove(claim);
            await db.SaveChangesAsync();
        }
        using var response = upload
            ? await client.PostAsync($"{h.PatientPath}/attachments", UploadBody(PdfBytes))
            : await client.GetAsync($"{h.PatientPath}/attachments");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        AssertNoObjects(h.Factory.Services);
    }

    [Theory]
    [InlineData("تقرير طبي.pdf")]
    [InlineData("with spaces.pdf")]
    [InlineData("double\"quote.pdf")]
    [InlineData("single'quote.pdf")]
    [InlineData("semi;colon.pdf")]
    [InlineData("percent%.pdf")]
    [InlineData("emoji😀.pdf")]
    [InlineData("carriage\rreturn.pdf")]
    [InlineData("line\nfeed.pdf")]
    [InlineData("back\\slash.pdf")]
    [InlineData("forward/slash.pdf")]
    [InlineData("LONG")]
    public void DispositionIsParseableAttachmentWithAsciiFallbackAndEncodedUnicode(string name)
    {
        if (name == "LONG") name = new string('a', 500) + ".pdf";
        var type = typeof(Clinic.Api.Controllers.StaffAttachmentsController);
        var method = type.GetMethod("BuildDisposition", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        var reference = new Clinic.Application.Attachments.AttachmentDownloadReference(Guid.NewGuid(), Guid.NewGuid(),
            "clinic-files/secret-key", name, "application/pdf");
        var header = (string)method.Invoke(null, [reference])!;
        var parsed = ContentDispositionHeaderValue.Parse(header);
        Assert.Equal("attachment", parsed.DispositionType);
        Assert.NotNull(parsed.FileNameStar);
        Assert.All(parsed.FileName!, c => Assert.InRange((int)c, 32, 126));
        Assert.DoesNotContain('\r', header);
        Assert.DoesNotContain('\n', header);
        Assert.DoesNotContain("secret-key", header);
        Assert.Equal(2, parsed.Parameters.Count);
    }

    [Theory]
    [InlineData(0, 20)]
    [InlineData(-1, 20)]
    [InlineData(1001, 20)]
    [InlineData(1, 0)]
    [InlineData(1, 101)]
    public async Task InvalidPaginationIsRejectedWithoutAudit(int page, int size)
    {
        using var h = await Seed();
        var (client, _) = await Client(h);
        using var owned = client;
        using var response = await client.GetAsync($"{h.PatientPath}/attachments?page={page}&pageSize={size}");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, h.Failure.Attempts);
    }
}
