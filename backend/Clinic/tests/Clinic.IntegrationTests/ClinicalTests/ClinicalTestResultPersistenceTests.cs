using Clinic.Domain.Entities;
using Clinic.Domain.Enums;
using Clinic.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Clinic.IntegrationTests.ClinicalTests;

public sealed class ClinicalTestResultPersistenceTests(SqlDatabaseFixture database) : IClassFixture<SqlDatabaseFixture>
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResultLinkIsUniqueAndBothForeignKeysPreventDeletion(bool sameRequest)
    {
        var now = DateTimeOffset.UtcNow;
        var patient = new Patient("Synthetic", "Synthetic"); var doctor = new Doctor("Synthetic");
        var request = new ClinicalTestRequest(patient.Id, null, doctor.Id, ClinicalTestCategory.Lab, "CBC", null, now);
        var other = new ClinicalTestRequest(patient.Id, null, doctor.Id, ClinicalTestCategory.Lab, "CBC", null, now);
        var file = new StoredFile($"clinic-files/{Guid.NewGuid():N}", "result.pdf", "application/pdf", 10, new string('a', 64), "actor", now);
        var attachment = new PatientAttachment(patient.Id, file.Id, null, "actor", now);
        var link = new ClinicalTestResultAttachment(request, attachment, now);
        await using (var db = database.CreateContext())
        {
            db.AddRange(patient, doctor, request, other, file, attachment, link); await db.SaveChangesAsync();
        }
        await using (var db = database.CreateContext())
        {
            db.Add(new ClinicalTestResultAttachment(sameRequest ? request : other, attachment, now));
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
        await using var fresh = database.CreateContext();
        var read = await fresh.Set<ClinicalTestResultAttachment>().SingleAsync(x => x.Id == link.Id);
        Assert.Equal(request.Id, read.ClinicalTestRequestId); Assert.Equal(attachment.Id, read.PatientAttachmentId); Assert.Equal(now, read.LinkedAtUtc);
        var model = fresh.Model.FindEntityType(typeof(ClinicalTestResultAttachment))!;
        Assert.Equal(2, model.GetForeignKeys().Count());
        Assert.All(model.GetForeignKeys(), x => Assert.Equal(DeleteBehavior.Restrict, x.DeleteBehavior));
        Assert.Contains(model.GetIndexes(), x => x.IsUnique && x.Properties.Single().Name == "PatientAttachmentId");
        var actions = await fresh.Database.SqlQueryRaw<int>("SELECT CONVERT(int, delete_referential_action) AS [Value] FROM sys.foreign_keys WHERE parent_object_id = OBJECT_ID(N'ClinicalTestResultAttachments')").ToArrayAsync();
        Assert.Equal(2, actions.Length); Assert.All(actions, x => Assert.Equal(0, x));
        await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(() => fresh.ClinicalTestRequests.Where(x => x.Id == request.Id).ExecuteDeleteAsync());
        await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(() => fresh.PatientAttachments.Where(x => x.Id == attachment.Id).ExecuteDeleteAsync());
    }

    [Theory]
    [InlineData(0, 1, null, false)]
    [InlineData(1, null, null, false)]
    [InlineData(2, null, null, false)]
    [InlineData(3, 1, null, true)]
    [InlineData(3, 1, 2, false)]
    [InlineData(1, -1, null, false)]
    [InlineData(3, 2, 1, true)]
    [InlineData(4, 1, 2, true)]
    [InlineData(2, 1, 2, true)]
    [InlineData(0, null, 1, false)]
    [InlineData(0, null, null, true)]
    [InlineData(1, 1, 2, false)]
    [InlineData(1, 1, null, true)]
    [InlineData(2, 1, null, true)]
    [InlineData(3, null, 2, true)]
    public async Task LifecycleConstraintRejectsInconsistentStates(int status, int? uploadOffset, int? reviewOffset, bool reviewer)
    {
        var now = DateTimeOffset.UtcNow;
        var patient = new Patient("Synthetic", "Synthetic"); var doctor = new Doctor("Synthetic");
        var request = new ClinicalTestRequest(patient.Id, null, doctor.Id, ClinicalTestCategory.Lab, "CBC", null, now);
        await using var db = database.CreateContext(); db.AddRange(patient, doctor, request); await db.SaveChangesAsync();
        DateTimeOffset? uploaded = uploadOffset is { } u ? now.AddMinutes(u) : null;
        DateTimeOffset? reviewed = reviewOffset is { } r ? now.AddMinutes(r) : null;
        Guid? reviewerId = reviewer ? doctor.Id : null;
        await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE ClinicalTestRequests SET Status={status}, UploadedAtUtc={uploaded}, ReviewedAtUtc={reviewed}, ReviewedByDoctorId={reviewerId} WHERE Id={request.Id}"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task LifecycleConstraintAlsoRejectsInvalidDirectSqlInserts(int status)
    {
        var patient = new Patient("Synthetic", "Synthetic"); var doctor = new Doctor("Synthetic"); var id = Guid.NewGuid();
        await using var db = database.CreateContext(); db.AddRange(patient, doctor); await db.SaveChangesAsync();
        var now = DateTimeOffset.UtcNow; DateTimeOffset? uploaded = status == 0 ? now : null;
        await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO ClinicalTestRequests (Id, PatientId, RequestedByDoctorId, Category, TestName, Status, RequestedAtUtc, UploadedAtUtc) VALUES ({id}, {patient.Id}, {doctor.Id}, 0, N'Invalid lifecycle', {status}, {now}, {uploaded})"));
        await using var fresh = database.CreateContext(); Assert.False(await fresh.ClinicalTestRequests.AnyAsync(x => x.Id == id));
    }
}
