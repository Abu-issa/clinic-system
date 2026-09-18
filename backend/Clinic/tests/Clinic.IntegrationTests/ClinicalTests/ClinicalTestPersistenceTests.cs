using System.Data.SqlTypes;
using Clinic.Application.ClinicalTests;
using Clinic.Domain.Entities;
using Clinic.Domain.Enums;
using Clinic.Infrastructure.Audit;
using Clinic.Infrastructure.Authentication;
using Clinic.Infrastructure.Persistence;
using Clinic.Infrastructure.Repositories;
using Clinic.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Clinic.IntegrationTests.ClinicalTests;

public sealed class ClinicalTestPersistenceTests(SqlDatabaseFixture database) : IClassFixture<SqlDatabaseFixture>
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RoundTripHasNullableVisitRowVersionAndFourRestrictForeignKeys(bool linked)
    {
        var patient = new Patient("Synthetic", "Synthetic");
        var doctor = new Doctor("Synthetic");
        var now = DateTimeOffset.UtcNow;
        var visit = new Visit(patient.Id, doctor.Id, null, now, "seed", now);
        var request = new ClinicalTestRequest(patient.Id, linked ? visit.Id : null, doctor.Id,
            ClinicalTestCategory.Imaging, "أشعة Chest X-Ray", "تعليمات Instructions", now);
        await using (var db = database.CreateContext())
        {
            db.AddRange(patient, doctor, visit, request);
            await db.SaveChangesAsync();
        }
        await using var fresh = database.CreateContext();
        var read = await fresh.ClinicalTestRequests.SingleAsync(x => x.Id == request.Id);
        Assert.Equal(request.TestName, read.TestName);
        Assert.Equal(request.ClinicalInstructions, read.ClinicalInstructions);
        Assert.Equal(request.VisitId, read.VisitId);
        Assert.Equal(ClinicalTestStatus.Requested, read.Status);
        Assert.Equal(8, read.RowVersion.Length);
        Assert.Null(read.UploadedAtUtc);
        Assert.Null(read.ReviewedAtUtc);
        Assert.Null(read.ReviewedByDoctorId);
        var model = fresh.Model.FindEntityType(typeof(ClinicalTestRequest))!;
        Assert.True(model.FindProperty(nameof(ClinicalTestRequest.RowVersion))!.IsConcurrencyToken);
        var fks = model.GetForeignKeys().ToArray();
        Assert.Equal(4, fks.Length);
        Assert.All(fks, x => Assert.Equal(DeleteBehavior.Restrict, x.DeleteBehavior));
        var actions = await fresh.Database.SqlQueryRaw<int>("SELECT CONVERT(int, delete_referential_action) AS [Value] FROM sys.foreign_keys WHERE parent_object_id = OBJECT_ID(N'ClinicalTestRequests')").ToArrayAsync();
        Assert.Equal(4, actions.Length);
        Assert.All(actions, x => Assert.Equal(0, x));
    }

    [Theory]
    [InlineData("patient")]
    [InlineData("doctor")]
    [InlineData("visit")]
    public async Task DatabaseNeverCascadesClinicalHistory(string parent)
    {
        var patient = new Patient("Synthetic", "Synthetic");
        var doctor = new Doctor("Synthetic");
        var now = DateTimeOffset.UtcNow;
        var visit = new Visit(patient.Id, doctor.Id, null, now, "seed", now);
        var request = new ClinicalTestRequest(patient.Id, parent == "visit" ? visit.Id : null, doctor.Id,
            ClinicalTestCategory.Lab, "CBC", null, now);
        await using (var db = database.CreateContext())
        {
            db.AddRange(patient, doctor, request);
            if (parent == "visit") db.Add(visit);
            await db.SaveChangesAsync();
        }
        await using (var db = database.CreateContext())
        {
            await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(async () =>
            {
                if (parent == "patient") await db.Patients.Where(x => x.Id == patient.Id).ExecuteDeleteAsync();
                else if (parent == "doctor") await db.Doctors.Where(x => x.Id == doctor.Id).ExecuteDeleteAsync();
                else await db.Set<Visit>().Where(x => x.Id == visit.Id).ExecuteDeleteAsync();
            });
        }
        await using var fresh = database.CreateContext();
        Assert.True(await fresh.ClinicalTestRequests.AnyAsync(x => x.Id == request.Id));
    }

    [Fact]
    public async Task HistoryOrderingFilteringAndPagingArePatientBounded()
    {
        var patient = new Patient("Synthetic", "Synthetic");
        var other = new Patient("Other", "Other");
        var doctor = new Doctor("Synthetic");
        var now = DateTimeOffset.UtcNow;
        var orders = new[]
        {
            new ClinicalTestRequest(patient.Id, null, doctor.Id, ClinicalTestCategory.Lab, "CBC", null, now),
            new ClinicalTestRequest(patient.Id, null, doctor.Id, ClinicalTestCategory.Imaging, "CT", null, now),
            new ClinicalTestRequest(patient.Id, null, doctor.Id, ClinicalTestCategory.Lab, "TSH", null, now.AddDays(-1)),
        };
        await using var db = database.CreateContext();
        db.AddRange(patient, other, doctor);
        db.AddRange(orders);
        db.Add(new ClinicalTestRequest(other.Id, null, doctor.Id, ClinicalTestCategory.Lab, "FOREIGN", null, now.AddDays(1)));
        await db.SaveChangesAsync();
        var store = new ClinicalTestStore(db);
        var first = await store.ListAsync(patient.Id, null, null, 1, 2, default);
        var second = await store.ListAsync(patient.Id, null, null, 2, 2, default);
        var expected = orders.OrderByDescending(x => x.RequestedAtUtc).ThenByDescending(x => new SqlGuid(x.Id)).Select(x => x.Id);
        Assert.Equal(expected, first.Concat(second).Select(x => x.Id));
        Assert.Equal(2, (await store.ListAsync(patient.Id, ClinicalTestCategory.Lab, ClinicalTestStatus.Requested, 1, 20, default)).Count);
        Assert.Empty(await store.ListAsync(patient.Id, null, ClinicalTestStatus.Uploaded, 1, 20, default));
        Assert.Null(await store.GetAsync(other.Id, orders[0].Id, default));
    }

    [Fact]
    public async Task ServiceRejectsCrossPatientVisitBeforeTrackingOrderOrAudit()
    {
        var patient = new Patient("Synthetic", "Synthetic");
        var other = new Patient("Other", "Other");
        var doctor = new Doctor("Synthetic");
        var now = DateTimeOffset.UtcNow;
        var visit = new Visit(other.Id, doctor.Id, null, now, "seed", now);
        var user = new StaffUser { Id = Guid.NewGuid().ToString(), AssociatedDoctorId = doctor.Id };
        await using var db = database.CreateContext();
        db.AddRange(patient, other, doctor, visit, user);
        await db.SaveChangesAsync();
        var service = new ClinicalTestService(new ClinicalTestStore(db), new AuditEventStore(db, TimeProvider.System), TimeProvider.System);
        var result = await service.CreateAsync(new(patient.Id, visit.Id, ClinicalTestCategory.Lab, "CBC", null), user.Id);
        Assert.Equal(ClinicalTestError.VisitNotFound, result.Error);
        Assert.Empty(db.ChangeTracker.Entries<ClinicalTestRequest>());
        Assert.Empty(db.ChangeTracker.Entries<AuditEvent>());
        await using var fresh = database.CreateContext();
        Assert.False(await fresh.ClinicalTestRequests.AnyAsync(x => x.PatientId == patient.Id));
        Assert.False(await fresh.AuditEvents.AnyAsync(x => x.PatientId == patient.Id));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task SqlRejectsPrematureResultStates(int status)
    {
        var patient = new Patient("Synthetic", "Synthetic");
        var doctor = new Doctor("Synthetic");
        var request = new ClinicalTestRequest(patient.Id, null, doctor.Id, ClinicalTestCategory.Lab, "CBC", null, DateTimeOffset.UtcNow);
        await using var db = database.CreateContext();
        db.AddRange(patient, doctor, request); await db.SaveChangesAsync();
        await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(() =>
            db.Database.ExecuteSqlInterpolatedAsync($"UPDATE ClinicalTestRequests SET Status = {status} WHERE Id = {request.Id}"));
        await using var fresh = database.CreateContext();
        Assert.Equal(ClinicalTestStatus.Requested, (await fresh.ClinicalTestRequests.SingleAsync(x => x.Id == request.Id)).Status);
    }
}
