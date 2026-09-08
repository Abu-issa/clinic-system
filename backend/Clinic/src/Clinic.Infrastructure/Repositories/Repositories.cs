using Clinic.Application.Abstractions;
using Clinic.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Clinic.Infrastructure.Repositories;

public sealed class PatientRepository : IPatientRepository
{
    private readonly ClinicDbContext _context;

    public PatientRepository(ClinicDbContext context)
    {
        _context = context;
    }

    public Task<bool> ExistsAsync(
        Guid patientId,
        CancellationToken cancellationToken = default)
    {
        return _context.Patients.AnyAsync(
            patient => patient.Id == patientId,
            cancellationToken);
    }
}
