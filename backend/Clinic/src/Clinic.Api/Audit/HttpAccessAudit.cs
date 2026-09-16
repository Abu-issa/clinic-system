using System.Security.Claims;
using Clinic.Application.Audit;
using Clinic.Domain.Enums;

namespace Clinic.Api.Audit;

public sealed class HttpAccessAudit(IAccessAuditWriter writer, ILogger<HttpAccessAudit> logger)
{
    public async Task<IResult?> RecordAsync(HttpContext context, string action, string resourceType,
        string resourceId, Guid? patientId, IReadOnlyCollection<KeyValuePair<string, string>>? metadata = null)
    {
        context.Response.Headers.CacheControl = "no-store";
        var actor = Actor(context.User);
        if (string.IsNullOrWhiteSpace(actor)) return Results.Unauthorized();
        await writer.WriteAsync(new(actor, action, resourceType, resourceId, patientId,
            AuditOutcome.Succeeded, Trace(context), metadata), context.RequestAborted);
        // Exceptions propagate to the sanitized global handler before any protected body is written.
        return null;
    }

    // Authentication availability is deliberately independent of standalone security auditing.
    // There is no queue or durable-delivery claim after a failure; operators must monitor this signal.
    public async Task SecurityAsync(HttpContext context, ClaimsPrincipal subject, string action)
    {
        var actor = Actor(subject);
        if (string.IsNullOrWhiteSpace(actor)) return;
        try
        {
            await writer.WriteAsync(new(actor, action, "staff-account", actor, null,
                AuditOutcome.Succeeded, Trace(context), null), context.RequestAborted);
        }
        catch (Exception)
        {
            logger.LogError("Security audit persistence failed. Action: {ActionCode}", action);
        }
    }

    private static string? Actor(ClaimsPrincipal principal) =>
        principal.FindFirstValue("staff_id") ?? principal.FindFirstValue(ClaimTypes.NameIdentifier);

    private static string Trace(HttpContext context) => context.TraceIdentifier.Length <= 128
        ? context.TraceIdentifier : context.TraceIdentifier[..128];
}
