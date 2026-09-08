using Clinic.Domain.Entities;

namespace Clinic.Application.Abstractions;

public interface IDoctorRepository
{
    Task<Doctor?> GetByIdAsync(
        Guid doctorId,
        CancellationToken cancellationToken = default);
}
