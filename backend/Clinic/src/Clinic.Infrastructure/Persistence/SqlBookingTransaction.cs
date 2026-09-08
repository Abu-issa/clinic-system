using System.Data;
using Clinic.Application.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Clinic.Infrastructure.Persistence;

public sealed class SqlBookingTransaction : IBookingTransaction
{
    private readonly ClinicDbContext _context;

    public SqlBookingTransaction(ClinicDbContext context)
    {
        _context = context;
    }

    public async Task<T> ExecuteAsync<T>(
        Guid doctorId,
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default)
    {
        if (doctorId == Guid.Empty)
        {
            throw new ArgumentException(
                "Doctor ID is required.",
                nameof(doctorId));
        }

        ArgumentNullException.ThrowIfNull(operation);

        if (_context.Database.CurrentTransaction is not null)
        {
            throw new InvalidOperationException(
                "A booking transaction is already active.");
        }

        await using var transaction =
            await _context.Database.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                cancellationToken);

        await using (var command =
            _context.Database.GetDbConnection().CreateCommand())
        {
            command.Transaction = transaction.GetDbTransaction();
            command.CommandTimeout = 30;

            command.CommandText = """
                DECLARE @result int;

                EXEC @result = sys.sp_getapplock
                    @Resource = @resource,
                    @LockMode = 'Exclusive',
                    @LockOwner = 'Transaction',
                    @LockTimeout = 10000,
                    @DbPrincipal = 'public';

                SELECT @result;
                """;

            var resourceParameter = command.CreateParameter();
            resourceParameter.ParameterName = "@resource";
            resourceParameter.DbType = DbType.String;
            resourceParameter.Size = 255;
            resourceParameter.Value = $"Clinic.Booking.Doctor:{doctorId:N}";

            command.Parameters.Add(resourceParameter);

            var value = await command.ExecuteScalarAsync(
                cancellationToken);

            if (value is not int lockResult)
            {
                throw new InvalidOperationException(
                    "SQL Server returned an invalid booking lock result.");
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (lockResult == -1)
            {
                throw new TimeoutException(
                    "Timed out waiting for the doctor's booking lock.");
            }

            if (lockResult < 0)
            {
                throw new InvalidOperationException(
                    $"Could not acquire the booking lock. Code: {lockResult}.");
            }
        }

        var result = await operation(cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        return result;
    }
}
