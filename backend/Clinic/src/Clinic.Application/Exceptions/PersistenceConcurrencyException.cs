namespace Clinic.Application.Exceptions;

public sealed class PersistenceConcurrencyException : Exception
{
    public PersistenceConcurrencyException(Exception innerException)
        : base(
            "The record changed before the operation could be saved.",
            innerException)
    {
    }
}