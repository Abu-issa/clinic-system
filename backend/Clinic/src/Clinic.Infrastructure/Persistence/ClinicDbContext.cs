using Clinic.Application.Abstractions;
using Clinic.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Clinic.Infrastructure.Persistence;

public sealed class ClinicDbContext : DbContext, IUnitOfWork
{
    public ClinicDbContext(
        DbContextOptions<ClinicDbContext> options)
        : base(options)
    {
    }

    public DbSet<Patient> Patients => Set<Patient>();

    public DbSet<Appointment> Appointments => Set<Appointment>();

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


            appointment.HasOne<Patient>()
                .WithMany()
                .HasForeignKey(x => x.PatientId)
                .OnDelete(DeleteBehavior.Restrict);

            appointment.HasIndex(x => x.StartsAtUtc);
        });
    }
}
