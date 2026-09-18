using Clinic.Application.Attachments;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Clinic.Infrastructure.Authentication;
using System.Security.Claims;

namespace Clinic.Api.Controllers;

// Runs before model binding, so neither multipart spooling nor IFormFile opening precedes CSRF.
[AttributeUsage(AttributeTargets.Method)]
public sealed class AttachmentUploadGateAttribute : Attribute, IAsyncResourceFilter
{
    public const long MultipartOverheadBytes = 65_536;

    public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        var http = context.HttpContext;
        var max = http.RequestServices.GetRequiredService<IOptions<AttachmentOptions>>().Value.MaxFileSizeBytes;
        var requestLimit = checked(max + MultipartOverheadBytes);
        var feature = http.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (feature is { IsReadOnly: false }) feature.MaxRequestBodySize = requestLimit;
        http.Features.Set<IFormFeature>(new FormFeature(http.Request, new FormOptions
        {
            MultipartBodyLengthLimit = requestLimit,
            ValueLengthLimit = (int)MultipartOverheadBytes,
            ValueCountLimit = 16,
            MultipartHeadersLengthLimit = 16_384,
        }));

        // Header-only CSRF avoids antiforgery's fallback to reading the multipart form.
        try
        {
            if (string.IsNullOrWhiteSpace(http.Request.Headers["X-CSRF-TOKEN"]))
                throw new AntiforgeryValidationException("Missing request token.");
            await http.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(http);
        }
        catch (AntiforgeryValidationException)
        {
            context.Result = new ObjectResult(new ProblemDetails
            {
                Status = 400, Title = "The request verification token is invalid.",
                Extensions = { ["code"] = "invalid_csrf_token", ["traceId"] = http.TraceIdentifier },
            }) { StatusCode = 400 };
            return;
        }
        var patientId = Guid.Parse(context.RouteData.Values["patientId"]!.ToString()!);
        var authorization = http.RequestServices.GetRequiredService<IAuthorizationService>();
        if (!(await authorization.AuthorizeAsync(http.User, patientId, "AttachmentWrite")).Succeeded)
        {
            context.Result = new ForbidResult();
            return;
        }
        if (context.RouteData.Values["visitId"] is { } routeVisit)
        {
            var store = http.RequestServices.GetRequiredService<IAttachmentStore>();
            var visit = await store.VisitAsync(patientId, Guid.Parse(routeVisit.ToString()!), http.RequestAborted);
            if (visit is null)
            {
                context.Result = new ObjectResult(new ProblemDetails
                {
                    Status = 404, Title = "The attachment request could not be completed.",
                    Extensions = { ["code"] = "attachment_not_found" },
                }) { StatusCode = 404 };
                return;
            }
            if (http.User.IsInRole("Doctor"))
            {
                var actor = http.User.FindFirstValue("staff_id") ?? http.User.FindFirstValue(ClaimTypes.NameIdentifier);
                var users = http.RequestServices.GetRequiredService<UserManager<StaffUser>>();
                var user = actor is null ? null : await users.FindByIdAsync(actor);
                if (user?.AssociatedDoctorId != visit.DoctorId)
                {
                    context.Result = new ForbidResult();
                    return;
                }
            }
        }
        var originalBody = http.Request.Body;
        http.Request.Body = new LimitedRequestStream(originalBody, requestLimit);
        try
        {
            var executed = await next();
            if (executed.Exception is BadHttpRequestException { StatusCode: 413 })
            {
                executed.ExceptionHandled = true;
                await Results.Problem(statusCode: 413, title: "The attachment request is too large.")
                    .ExecuteAsync(http);
            }
        }
        finally { http.Request.Body = originalBody; }
    }

    // Bounds total actual multipart bytes even on hosts without a mutable server size feature.
    // Does not own the server request stream and never buffers or seeks.
    private sealed class LimitedRequestStream(Stream inner, long limit) : Stream
    {
        private long consumed;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        private int Count(int read)
        {
            consumed += read;
            if (consumed > limit) throw new BadHttpRequestException("Attachment request limit exceeded.", 413);
            return read;
        }
        private int Allow(int requested) => (int)Math.Min(requested, Math.Max(1, limit - consumed + 1));
        public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, Allow(count)));
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
            => Count(await inner.ReadAsync(buffer[..Allow(buffer.Length)], ct));
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
            => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
