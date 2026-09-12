using Clinic.Application.Abstractions;
using Clinic.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Clinic.Application.Exceptions;

namespace Clinic.Infrastructure.Persistence;

public sealed class ClinicDbContext : DbContext, IUnitOfWork
{
    public ClinicDbContext(
        DbContextOptions<ClinicDbContext> options)
        : base(options)
    {
    }

    public DbSet<Patient> Patients => Set<Patient>();
    public DbSet<Doctor> Doctors => Set<Doctor>();

    public DbSet<Appointment> Appointments => Set<Appointment>();
    public DbSet<DoctorWorkingPeriod> DoctorWorkingPeriods =>
    Set<DoctorWorkingPeriod>();
    public DbSet<DoctorDayClosure> DoctorDayClosures =>
    Set<DoctorDayClosure>();
    public DbSet<AppointmentReschedule> AppointmentReschedules =>
    Set<AppointmentReschedule>();
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Patient>(patient =>
        {
            patient.ToTable("Patients");

            patient.HasKey(x => x.Id);

            patient.Property(x => x.Id)
                .ValueGeneratedNever();

            patient.Property(x => x.FullName)
                .IsRequired();

            patient.Property(x => x.PhoneNumber)
                .IsRequired();
        });

        modelBuilder.Entity<Appointment>(appointment =>
        {
            appointment.ToTable(
                "Appointments",
                table => table.HasCheckConstraint(
                    "CK_Appointments_TimeRange",
                    "[EndsAtUtc] > [StartsAtUtc]"));

            appointment.HasKey(x => x.Id);

            appointment.Property(x => x.Id)
                .ValueGeneratedNever();

            appointment.Property(x => x.Status)
                .HasConversion<int>()
                .IsRequired();
            appointment.Property(x => x.CancellationReason)
    .HasMaxLength(500);

            appointment.Property(x => x.CancelledByUserId)
                .HasMaxLength(200);

            appointment.Property(x => x.CancelledAtUtc)
                .HasColumnType("datetimeoffset");
            appointment.Property(x => x.RowVersion)
    .IsRowVersion();

            appointment.HasOne<Patient>()
                .WithMany()
                .HasForeignKey(x => x.PatientId)
                .OnDelete(DeleteBehavior.Restrict);
            appointment.HasOne<Doctor>()
    .WithMany()
    .HasForeignKey(x => x.DoctorId)
    .OnDelete(DeleteBehavior.Restrict);

            appointment.HasIndex(x => new
            {
                x.DoctorId,
                x.StartsAtUtc
            });
        });
        modelBuilder.Entity<Doctor>(doctor =>
        {
            doctor.ToTable("Doctors");

            doctor.HasKey(x => x.Id);

            doctor.Property(x => x.Id)
                .ValueGeneratedNever();

            doctor.Property(x => x.FullName)
                .IsRequired();

            doctor.Property(x => x.IsActive)
                .IsRequired();
        });
        modelBuilder.Entity<DoctorWorkingPeriod>(period =>
        {
            period.ToTable(
                "DoctorWorkingPeriods",
                table =>
                {
                    table.HasCheckConstraint(
                        "CK_DoctorWorkingPeriods_TimeRange",
                        "[EndsAtLocal] > [StartsAtLocal]");

                    table.HasCheckConstraint(
                        "CK_DoctorWorkingPeriods_DayOfWeek",
                        "[DayOfWeek] BETWEEN 0 AND 6");
                });

            period.HasKey(x => x.Id);

            period.Property(x => x.Id)
                .ValueGeneratedNever();

            period.Property(x => x.DayOfWeek)
                .HasConversion<int>()
                .IsRequired();

            period.Property(x => x.StartsAtLocal)
                .HasColumnType("time")
                .IsRequired();

            period.Property(x => x.EndsAtLocal)
                .HasColumnType("time")
                .IsRequired();

            period.Property(x => x.IsActive)
                .IsRequired();

            period.HasOne<Doctor>()
                .WithMany()
                .HasForeignKey(x => x.DoctorId)
                .OnDelete(DeleteBehavior.Restrict);

            period.HasIndex(x => new
            {
                x.DoctorId,
                x.DayOfWeek,
                x.IsActive
            });
        });
        modelBuilder.Entity<DoctorDayClosure>(closure =>
        {
            closure.ToTable("DoctorDayClosures");

            closure.HasKey(x => x.Id);

            closure.Property(x => x.Id)
                .ValueGeneratedNever();

            closure.Property(x => x.LocalDate)
                .HasColumnType("date")
                .IsRequired();

            closure.Property(x => x.Reason)
                .HasMaxLength(500)
                .IsRequired();

            closure.HasOne<Doctor>()
                .WithMany()
                .HasForeignKey(x => x.DoctorId)
                .OnDelete(DeleteBehavior.Restrict);

            closure.HasIndex(x => new
            {
                x.DoctorId,
                x.LocalDate
            })
                .IsUnique();
        });
        modelBuilder.Entity<AppointmentReschedule>(change =>
        {
            change.ToTable(
                "AppointmentReschedules",
                table =>
                {
                    table.HasCheckConstraint(
                        "CK_AppointmentReschedules_PreviousTimeRange",
                        "[PreviousEndsAtUtc] > [PreviousStartsAtUtc]");

                    table.HasCheckConstraint(
                        "CK_AppointmentReschedules_NewTimeRange",
                        "[NewEndsAtUtc] > [NewStartsAtUtc]");
                });

            change.HasKey(x => x.Id);

            change.Property(x => x.Id)
                .ValueGeneratedNever();

            change.Property(x => x.PreviousStartsAtUtc)
                .HasColumnType("datetimeoffset")
                .IsRequired();

            change.Property(x => x.PreviousEndsAtUtc)
                .HasColumnType("datetimeoffset")
                .IsRequired();

            change.Property(x => x.NewStartsAtUtc)
                .HasColumnType("datetimeoffset")
                .IsRequired();

            change.Property(x => x.NewEndsAtUtc)
                .HasColumnType("datetimeoffset")
                .IsRequired();

            change.Property(x => x.ChangedAtUtc)
                .HasColumnType("datetimeoffset")
                .IsRequired();

            change.Property(x => x.Reason)
                .HasMaxLength(500)
                .IsRequired();

            change.Property(x => x.ChangedByUserId)
                .HasMaxLength(200)
                .IsRequired();

            change.HasOne<Appointment>()
                .WithMany()
                .HasForeignKey(x => x.AppointmentId)
                .OnDelete(DeleteBehavior.Restrict);

            change.HasIndex(x => new
            {
                x.AppointmentId,
                x.ChangedAtUtc
            });
        });
    }
    public override async Task<int> SaveChangesAsync(
    CancellationToken cancellationToken = default)
    {
        try
        {
            return await base.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException exception)
        {
            throw new PersistenceConcurrencyException(exception);
        }
    }

}
