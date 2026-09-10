using Microsoft.AspNetCore.Diagnostics;

namespace Clinic.Api.ErrorHandling;

public sealed class GlobalExceptionHandler : IExceptionHandler
{
    private readonly ILogger<GlobalExceptionHandler> _logger;

    public GlobalExceptionHandler(
        ILogger<GlobalExceptionHandler> logger)
    {
        _logger = logger;
    }

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var traceId = httpContext.TraceIdentifier;

        _logger.LogError(
            "Unhandled API exception. Type: {ExceptionType}. TraceId: {TraceId}",
            exception.GetType().FullName,
            traceId);

        var problem = Results.Problem(
            statusCode: StatusCodes.Status500InternalServerError,
            title: "An unexpected error occurred.",
            detail: "The request could not be completed.",
            extensions: new Dictionary<string, object?>
            {
                ["code"] = "unexpected_error",
                ["traceId"] = traceId
            });

        await problem.ExecuteAsync(httpContext);

        return true;
    }
}
