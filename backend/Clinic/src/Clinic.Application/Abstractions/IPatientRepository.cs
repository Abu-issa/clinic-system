namespace Clinic.Application.Abstractions;

public interface IPatientRepository
{
    Task<bool> ExistsAsync(
        Guid patientId,
        CancellationToken cancellationToken = default);
}
