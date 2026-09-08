namespace Clinic.Application.Abstractions;

public interface IBookingTransaction
{
    Task<T> ExecuteAsync<T>(
        Guid doctorId,
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default);
}
