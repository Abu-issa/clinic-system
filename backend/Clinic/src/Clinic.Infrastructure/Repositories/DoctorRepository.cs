using Clinic.Application.Abstractions;
using Clinic.Domain.Entities;
using Clinic.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Clinic.Infrastructure.Repositories;

public sealed class DoctorRepository : IDoctorRepository
{
    private readonly ClinicDbContext _context;

    public DoctorRepository(ClinicDbContext context)
    {
        _context = context;
    }

    public Task<Doctor?> GetByIdAsync(
        Guid doctorId,
        CancellationToken cancellationToken = default)
    {
        return _context.Doctors
            .AsNoTracking()
            .SingleOrDefaultAsync(
                doctor => doctor.Id == doctorId,
                cancellationToken);
    }
}
