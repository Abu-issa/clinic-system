using Clinic.Application.Appointments;
using Clinic.Application.Audit;
using Clinic.Application.Medications;
using Clinic.Application.Patients;
using Clinic.Application.Prescriptions;
using Clinic.Domain.Entities;
using Clinic.Domain.Enums;
using Clinic.Infrastructure.Audit;
using Clinic.Infrastructure.Authentication;
using Clinic.Infrastructure.Persistence;
using Clinic.Infrastructure.Repositories;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Clinic.IntegrationTests.Infrastructure;

public sealed partial class AuditMutationIntegrationTests
{
    [Fact]
    public async Task VisitSqlConcurrencyLoserDiscardsAuditAndCanRetryOnce()
    {
        await using var loser = database.CreateContext();
        var (patient, _, visit) = await SeedVisitAsync(loser);
        var store = new VisitStore(loser);
        await store.GetAsync(patient.Id, visit.Id, default); // Load collections before the other context wins.
        var stale = visit.RowVersion.ToArray();
        byte[] current;
        await using (var winner = database.CreateContext())
        {
            var result = await Services(winner).Visits.UpdateAsync(patient.Id, visit.Id,
                new(new VisitClinicalContent(null, null, "winner", null, null, null, null), stale), "doctor");
            Assert.True(result.IsSuccess);
            current = result.Details!.RowVersion;
        }
        var service = Services(loser).Visits;
        Assert.Equal(Clinic.Application.Visits.VisitError.VisitChanged,
            (await service.FinalizeAsync(patient.Id, visit.Id, stale, "doctor")).Error);
        await loser.SaveChangesAsync();
        await using var verify = database.CreateContext();
        Assert.False(await verify.Set<AuditEvent>().AnyAsync(x => x.ResourceId == visit.Id.ToString("N")));
        Assert.Equal(VisitStatus.Draft, (await verify.Set<Visit>().AsNoTracking().SingleAsync(x => x.Id == visit.Id)).Status);
        Assert.True((await service.FinalizeAsync(patient.Id, visit.Id, current, "doctor")).IsSuccess);
        var e = Assert.Single(await verify.Set<AuditEvent>().Where(x => x.ResourceId == visit.Id.ToString("N")).ToListAsync());
        Assert.Equal("visit.finalize", e.ActionCode);
        Assert.Equal(AuditOutcome.Succeeded, e.Outcome);
    }

    [Fact]
    public async Task InvalidMedicationDoesNotPersistBusinessOrAuditRows()
    {
        await using var db = database.CreateContext();
        var actor = "invalid-" + Guid.NewGuid().ToString("N");
        Assert.Equal(MedicationCatalogError.InvalidInput, (await Services(db).Catalog.CreateAsync(
            new(null, null, null, null, "500", "mg", DosageForm.Tablet, MedicationRoute.Oral, null), actor)).Error);
        await db.SaveChangesAsync();
        await using var verify = database.CreateContext();
        Assert.False(await verify.Set<AuditEvent>().AnyAsync(x => x.ActorStaffId == actor));
        Assert.False(await verify.Medications.AnyAsync(x => x.CreatedByStaffId == actor));
    }

    [Fact]
    public async Task PatientProfileAndStaleAdminUpdateHaveExactSemantics()
    {
        await using var db = database.CreateContext();
        var s = Services(db);
        var (patient, _, _) = await SeedVisitAsync(db);
        var stale = patient.RowVersion.ToArray();
        Assert.True((await s.Patients.UpdateAsync(patient.Id,
            new("winner", "phone", null, null, null, null, stale), "admin")).IsSuccess);
        Assert.Equal(PatientAdminError.PatientChanged, (await s.Patients.UpdateAsync(patient.Id,
            new("loser", "phone", null, null, null, null, stale), "admin")).Error);
        Assert.True((await s.Patients.SaveProfileAsync(patient.Id,
            new(null, AllergyStatus.Unknown, null, null, [], [], [], [], [], null), "doctor")).IsSuccess);
        await db.SaveChangesAsync();
        await using var verify = database.CreateContext();
        Assert.Equal("winner", (await verify.Patients.SingleAsync(x => x.Id == patient.Id)).FullName);
        var events = await verify.Set<AuditEvent>().Where(x => x.PatientId == patient.Id).ToListAsync();
        Assert.Equal(new[] { "patient.admin.update", "patient.clinical-profile.update" }, events.Select(x => x.ActionCode).Order());
        var profile = events.Single(x => x.ActionCode == "patient.clinical-profile.update");
        Assert.Equal("patient", profile.ResourceType);
        Assert.Equal(patient.Id.ToString("N"), profile.ResourceId);
        Assert.Equal("doctor", profile.ActorStaffId);
        Assert.Equal(AuditOutcome.Succeeded, profile.Outcome);
        Assert.Equal("0", Assert.Single(profile.Metadata).Value);
        Assert.True(profile.Metadata.ContainsKey("entry-count"));
        Assert.True(await verify.Set<PatientMedicalProfile>().AnyAsync(x => x.PatientId == patient.Id));
    }

    [Fact]
    public async Task PrescriptionRemoveAndInvalidFinalizeDoNotDuplicateEvents()
    {
        await using var db = database.CreateContext();
        var s = Services(db);
        var (_, _, visit) = await SeedVisitAsync(db);
        var medication = await SeedMedicationAsync(db, "remove");
        var draft = (await s.Prescriptions.CreateDraftAsync(new(visit.Id), "doctor")).Details!;
        draft = (await s.Prescriptions.AddItemAsync(visit.PatientId, draft.Id,
            new(medication.Id, "dose", "frequency", "duration", null, null, draft.RowVersion), "doctor")).Details!;
        var removed = await s.Prescriptions.RemoveItemAsync(visit.PatientId, draft.Id, draft.Items.Single().Id, draft.RowVersion, "doctor");
        Assert.True(removed.IsSuccess);
        Assert.Equal(PrescriptionError.InvalidInput,
            (await s.Prescriptions.FinalizeAsync(visit.PatientId, draft.Id, removed.Details!.RowVersion, "doctor")).Error);
        await db.SaveChangesAsync();
        await using var verify = database.CreateContext();
        var saved = await verify.Set<Prescription>().Include(x => x.Items).SingleAsync(x => x.Id == draft.Id);
        Assert.Empty(saved.Items);
        Assert.Equal(PrescriptionStatus.Draft, saved.Status);
        var events = await verify.Set<AuditEvent>().Where(x => x.ResourceId == draft.Id.ToString("N")).ToListAsync();
        Assert.Equal(new[] { "prescription.create", "prescription.item.add", "prescription.item.remove" }, events.Select(x => x.ActionCode).Order());
        var removedEvent = events.Single(x => x.ActionCode == "prescription.item.remove");
        Assert.Equal("prescription", removedEvent.ResourceType);
        Assert.Equal(visit.PatientId, removedEvent.PatientId);
        Assert.Equal("doctor", removedEvent.ActorStaffId);
        Assert.Equal(AuditOutcome.Succeeded, removedEvent.Outcome);
        Assert.Equal("0", removedEvent.Metadata["item.count"]);
    }

    [Theory]
    [InlineData("visit")]
    [InlineData("patient")]
    [InlineData("medication")]
    [InlineData("prescription")]
    public async Task AuditFailureDiscardsOnlyOwnedEventsAndRollsBackMutation(string module)
    {
        var interceptor = new FailingAuditInsertInterceptor();
        await using var db = new ClinicDbContext(new DbContextOptionsBuilder<ClinicDbContext>()
            .UseSqlServer(database.ConnectionString).AddInterceptors(interceptor).Options);
        var s = Services(db);
        var (patient, _, visit) = await SeedVisitAsync(db);
        var medication = await SeedMedicationAsync(db, "rollback");
        var prescription = (await s.Prescriptions.CreateDraftAsync(new(visit.Id, "before"), "doctor")).Details!;
        var history = await db.Set<AuditEvent>().SingleAsync(x => x.ResourceId == prescription.Id.ToString("N"));
        var unrelatedId = Guid.NewGuid().ToString("N");
        new AuditEventStore(db, new FixedClock()).Append(new("system", "test.pending", "test-resource", unrelatedId,
            null, AuditOutcome.Succeeded, null, null));
        var unrelated = db.ChangeTracker.Entries<AuditEvent>().Single(x => x.State == EntityState.Added).Entity;
        interceptor.Enabled = true;
        var error = await Assert.ThrowsAsync<DbUpdateException>(async () =>
        {
            switch (module)
            {
                case "visit": await s.Visits.FinalizeAsync(patient.Id, visit.Id, visit.RowVersion, "doctor"); break;
                case "patient": await s.Patients.UpdateAsync(patient.Id,
                    new("rejected", "phone", null, null, null, null, patient.RowVersion), "admin"); break;
                case "medication": await s.Catalog.DeactivateAsync(medication.Id, medication.RowVersion, "admin"); break;
                case "prescription": await s.Prescriptions.UpdateDraftNotesAsync(patient.Id, prescription.Id,
                    new("rejected", prescription.RowVersion), "doctor"); break;
            }
        });
        Assert.IsType<AuditInsertFailureException>(error.InnerException);
        Assert.Same(unrelated, Assert.Single(db.ChangeTracker.Entries<AuditEvent>(), x => x.State == EntityState.Added).Entity);
        Assert.Equal(EntityState.Unchanged, db.Entry(history).State);
        interceptor.Enabled = false;
        await db.SaveChangesAsync(); // Must persist only the unrelated pending event, never the failed operation.
        await using var verify = database.CreateContext();
        Assert.Equal(VisitStatus.Draft, (await verify.Set<Visit>().SingleAsync(x => x.Id == visit.Id)).Status);
        Assert.Equal("Audit Visit Patient", (await verify.Patients.SingleAsync(x => x.Id == patient.Id)).FullName);
        Assert.True((await verify.Medications.SingleAsync(x => x.Id == medication.Id)).IsActive);
        Assert.Equal("before", (await verify.Set<Prescription>().SingleAsync(x => x.Id == prescription.Id)).Notes);
        Assert.Equal(new[] { "prescription.create" }, (await verify.Set<AuditEvent>().Where(x => x.PatientId == patient.Id).ToListAsync()).Select(x => x.ActionCode));
        Assert.False(await verify.Set<AuditEvent>().AnyAsync(x => x.ResourceId == medication.Id.ToString("N")));
        Assert.True(await verify.Set<AuditEvent>().AnyAsync(x => x.ResourceId == unrelatedId));
    }

    [Fact]
    public async Task AppointmentBookingReschedulingCancellationAndRejectionsAreAudited()
    {
        await using var db = database.CreateContext();
        var (patient, doctor, _) = await SeedVisitAsync(db);
        db.AddRange(TestWorkingHours.CreatePeriods(doctor.Id));
        await db.SaveChangesAsync();
        var clock = new FixedClock();
        var writer = new AuditEventStore(db, clock);
        var appointments = new AppointmentRepository(db);
        var transaction = new SqlBookingTransaction(db);
        var schedule = new WorkingScheduleRepository(db, TestWorkingHours.ClinicTimeZone);
        var booking = new AppointmentBookingService(new PatientRepository(db), new DoctorRepository(db), appointments,
            db, transaction, schedule, clock, TestWorkingHours.Policy, writer);
        var start = new DateTimeOffset(2026, 9, 20, 7, 0, 0, TimeSpan.Zero);
        var request = new BookAppointmentRequest(patient.Id, doctor.Id, start, AppointmentType.Consultation);
        var booked = await booking.BookAsync(request, "scheduler");
        Assert.True(booked.IsSuccess, booked.Error.ToString());
        Assert.Equal(BookingError.TimeSlotUnavailable, (await booking.BookAsync(request, "scheduler")).Error);
        var appointment = await db.Appointments.SingleAsync(x => x.Id == booked.AppointmentId);
        var originalVersion = appointment.RowVersion.ToArray();
        var rescheduling = new AppointmentReschedulingService(appointments, new DoctorRepository(db), schedule,
            db, transaction, clock, TestWorkingHours.Policy, writer);
        var moved = await rescheduling.RescheduleAsync(new(appointment.Id, doctor.Id, start.AddHours(1),
            appointment.EndsAtUtc.AddHours(1), "private reschedule reason", originalVersion), "scheduler");
        Assert.True(moved.IsSuccess, moved.Error.ToString());
        Assert.Equal(ReschedulingError.AppointmentChanged, (await rescheduling.RescheduleAsync(new(appointment.Id,
            doctor.Id, start.AddHours(2), appointment.EndsAtUtc.AddHours(1), "stale", originalVersion), "scheduler")).Error);
        var cancellation = new AppointmentCancellationService(appointments, db, transaction, clock, writer);
        var cancelRequest = new CancelAppointmentRequest(appointment.Id, doctor.Id, "private cancellation reason");
        Assert.True((await cancellation.CancelAsync(cancelRequest, "scheduler")).IsSuccess);
        Assert.Equal(CancellationError.AppointmentCannotBeCancelled, (await cancellation.CancelAsync(cancelRequest, "scheduler")).Error);
        await using var verify = database.CreateContext();
        var events = await verify.Set<AuditEvent>().Where(x => x.ResourceId == appointment.Id.ToString("N")).ToListAsync();
        Assert.Equal(new[] { "appointment.cancel", "appointment.create", "appointment.reschedule" }, events.Select(x => x.ActionCode).Order());
        Assert.All(events, e =>
        {
            Assert.Equal("appointment", e.ResourceType);
            Assert.Equal(patient.Id, e.PatientId);
            Assert.Equal("scheduler", e.ActorStaffId);
            Assert.Equal(AuditOutcome.Succeeded, e.Outcome);
        });
        Assert.Equal("Consultation", events.Single(x => x.ActionCode == "appointment.create").Metadata["appointment-type"]);
        Assert.Equal(moved.ChangeId!.Value.ToString("N"), events.Single(x => x.ActionCode == "appointment.reschedule").Metadata["reschedule.id"]);
        Assert.Empty(events.Single(x => x.ActionCode == "appointment.cancel").Metadata);
        var saved = await verify.Appointments.SingleAsync(x => x.Id == appointment.Id);
        Assert.Equal(AppointmentStatus.Cancelled, saved.Status);
        Assert.Equal(start.AddHours(1), saved.StartsAtUtc);
        Assert.True(await verify.Set<AppointmentReschedule>().AnyAsync(x => x.Id == moved.ChangeId));
    }

    [Theory]
    [InlineData("disable", "staff.disable")]
    [InlineData("revoke", "staff.sessions-revoke")]
    [InlineData("reset-password", "staff.password-reset")]
    [InlineData("reset-mfa", "staff.mfa-reset")]
    [InlineData("set-grants", "staff.grants.change")]
    public async Task StaffIdentityAndAuditShareTheTransaction(string operation, string action)
    {
        await using var provider = StaffProvider(database.ConnectionString);
        string id;
        string? oldStamp;
        using (var seedScope = provider.CreateScope())
        {
            var users = seedScope.ServiceProvider.GetRequiredService<UserManager<StaffUser>>();
            var user = new StaffUser { UserName = "audit-" + Guid.NewGuid().ToString("N"), IsEnabled = true };
            Assert.True((await users.CreateAsync(user, "Synthetic-Password-93!")).Succeeded);
            id = user.Id;
            oldStamp = user.SecurityStamp;
        }
        using (var mutationScope = provider.CreateScope())
            await mutationScope.ServiceProvider.GetRequiredService<StaffAdministration>().ChangeAsync(id, operation,
                "Changed-Password-94!", ["Doctor"], [], [], [], "operator");
        await using var verify = database.CreateContext();
        var e = await verify.Set<AuditEvent>().SingleAsync(x => x.ResourceId == id);
        Assert.Equal(action, e.ActionCode);
        Assert.Equal("staff-account", e.ResourceType);
        Assert.Null(e.PatientId);
        Assert.Equal("operator", e.ActorStaffId);
        Assert.Equal(AuditOutcome.Succeeded, e.Outcome);
        var saved = await verify.Users.SingleAsync(x => x.Id == id);
        Assert.NotEqual(oldStamp, saved.SecurityStamp);
        Assert.Equal(operation != "disable", saved.IsEnabled);
        if (operation == "set-grants")
        {
            Assert.Equal(3, e.Metadata.Count);
            Assert.Equal("1", e.Metadata["role-count"]);
            Assert.Equal("0", e.Metadata["permission-count"]);
            Assert.Equal("0", e.Metadata["patient-scope-count"]);
        }
        else Assert.Empty(e.Metadata);
    }

    [Fact]
    public async Task StaffAuditInsertFailureRollsBackAlreadySavedIdentityWrites()
    {
        var interceptor = new FailingAuditInsertInterceptor();
        await using var provider = StaffProvider(database.ConnectionString, interceptor);
        string id;
        string? stamp;
        using (var scope = provider.CreateScope())
        {
            var user = new StaffUser { UserName = "rollback-" + Guid.NewGuid().ToString("N"), IsEnabled = true };
            Assert.True((await scope.ServiceProvider.GetRequiredService<UserManager<StaffUser>>()
                .CreateAsync(user, "Synthetic-Password-93!")).Succeeded);
            id = user.Id;
            stamp = user.SecurityStamp;
        }
        interceptor.Enabled = true;
        using (var scope = provider.CreateScope())
        {
            var error = await Assert.ThrowsAsync<DbUpdateException>(() =>
                scope.ServiceProvider.GetRequiredService<StaffAdministration>().ChangeAsync(id, "disable"));
            Assert.IsType<AuditInsertFailureException>(error.InnerException);
            Assert.Empty(scope.ServiceProvider.GetRequiredService<ClinicDbContext>().ChangeTracker.Entries<AuditEvent>());
        }
        await using var verify = database.CreateContext();
        var saved = await verify.Users.SingleAsync(x => x.Id == id);
        Assert.True(saved.IsEnabled);
        Assert.Equal(stamp, saved.SecurityStamp);
        Assert.False(await verify.Set<AuditEvent>().AnyAsync(x => x.ResourceId == id));
    }

    [Fact]
    public async Task StaffProvisionCommitsOneEventAndIdempotentRerunAddsNone()
    {
        var isolated = new SqlDatabaseFixture();
        await isolated.InitializeAsync();
        try
        {
            await using var provider = StaffProvider(isolated.ConnectionString);
            string id;
            using (var scope = provider.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ClinicDbContext>();
                var doctor = new Doctor("provision");
                db.Add(doctor);
                await db.SaveChangesAsync();
                var admin = scope.ServiceProvider.GetRequiredService<StaffAdministration>();
                id = await admin.ProvisionFirstDoctorAsync("provision", "Synthetic-Password-93!", doctor.Id, [doctor.Id]);
                Assert.Equal(id, await admin.ProvisionFirstDoctorAsync("provision", "Unused-Password-94!", doctor.Id, [doctor.Id]));
            }
            await using var verify = isolated.CreateContext();
            var e = Assert.Single(await verify.Set<AuditEvent>().ToListAsync());
            Assert.Equal("staff.provision", e.ActionCode);
            Assert.Equal("staff-account", e.ResourceType);
            Assert.Equal(id, e.ResourceId);
            Assert.Null(e.ActorStaffId);
            Assert.Null(e.PatientId);
            Assert.Equal(AuditOutcome.Succeeded, e.Outcome);
            Assert.Equal("1", e.Metadata["scope-count"]);
            Assert.True(await verify.Users.AnyAsync(x => x.Id == id));
        }
        finally { await isolated.DisposeAsync(); }
    }

    private static ServiceProvider StaffProvider(string connectionString, FailingAuditInsertInterceptor? interceptor = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataProtection();
        services.AddSingleton<TimeProvider>(new FixedClock());
        services.AddDbContext<ClinicDbContext>(options =>
        {
            options.UseSqlServer(connectionString);
            if (interceptor is not null) options.AddInterceptors(interceptor);
        });
        services.AddScoped<IAuditMutationWriter, AuditEventStore>();
        services.AddStaffIdentity();
        return services.BuildServiceProvider();
    }
}
