using System.Security.Claims;
using Clinic.Api.Audit;
using Clinic.Api.Controllers;
using Clinic.Application.Attachments;
using Clinic.Application.Audit;
using Clinic.Application.Storage;
using Clinic.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Clinic.UnitTests;

public sealed class AttachmentDownloadGateTests
{
    private sealed class Authorization(List<string> order) : IAuthorizationService
    {
        public Task<AuthorizationResult> AuthorizeAsync(ClaimsPrincipal user, object? resource, IEnumerable<IAuthorizationRequirement> requirements)
            => throw new NotSupportedException();
        public Task<AuthorizationResult> AuthorizeAsync(ClaimsPrincipal user, object? resource, string policyName)
        { order.Add("authorize"); return Task.FromResult(AuthorizationResult.Success()); }
    }
    private sealed class Store(List<string> order) : IAttachmentStore
    {
        public Task<AttachmentDownloadReference?> DownloadAsync(Guid patient, Guid attachment, CancellationToken ct = default)
        { order.Add("metadata"); return Task.FromResult<AttachmentDownloadReference?>(new(attachment, Guid.NewGuid(), "private-key", "file.pdf", "application/pdf")); }
        public Task<bool> PatientExistsAsync(Guid id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AttachmentVisitReference?> VisitAsync(Guid patient, Guid visit, CancellationToken ct = default) => throw new NotSupportedException();
        public void Add(StoredFile file, PatientAttachment attachment) => throw new NotSupportedException();
        public Task SaveAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public void DiscardChanges() => throw new NotSupportedException();
        public Task<IReadOnlyList<AttachmentListItem>> ListAsync(Guid patient, Guid? visit, int page, int size, CancellationToken ct = default) => throw new NotSupportedException();
    }
    private sealed class Content : MemoryStream
    {
        public bool Disposed;
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
    private sealed class Storage(List<string> order, Content content, bool missing) : IFileStorage
    {
        public Task<Stream?> OpenReadAsync(string key, CancellationToken ct = default)
        { order.Add("open"); return Task.FromResult<Stream?>(missing ? null : content); }
        public Task<StagedObject> StageAsync(Stream stream, CancellationToken ct = default) => throw new NotSupportedException();
        public Task PromoteAsync(string staged, string key, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteAsync(string key, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> ExistsAsync(string key, CancellationToken ct = default) => throw new NotSupportedException();
    }
    private sealed class Audit(List<string> order, bool fail) : IAccessAuditWriter
    {
        public Task WriteAsync(AuditAppendRequest request, CancellationToken ct = default)
        {
            order.Add("audit");
            Assert.Equal("file.download", request.ActionCode);
            if (fail) throw new IOException("audit outage");
            return Task.CompletedTask;
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task DownloadOrdersDisclosureAfterDurableAuditAndDisposesOnFailure(bool fail, bool missing)
    {
        var order = new List<string>();
        using var content = new Content();
        var controller = new StaffAttachmentsController(null!, new Store(order), new Storage(order, content, missing),
            new Authorization(order), null!, new HttpAccessAudit(new Audit(order, fail), NullLogger<HttpAccessAudit>.Instance),
            null!, Options.Create(new AttachmentOptions()));
        var http = new DefaultHttpContext();
        http.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("staff_id", "test")], "test"));
        http.Response.Body = new MemoryStream();
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        if (fail)
        {
            await Assert.ThrowsAsync<IOException>(() => controller.Download(Guid.NewGuid(), Guid.NewGuid(), default));
            Assert.True(content.Disposed);
            Assert.False(http.Response.Headers.ContainsKey("Content-Disposition"));
        }
        else
        {
            var result = await controller.Download(Guid.NewGuid(), Guid.NewGuid(), default);
            Assert.NotNull(result);
            Assert.Equal(!missing, http.Response.Headers.ContainsKey("Content-Disposition"));
        }
        Assert.Equal(0, http.Response.Body.Length);
        Assert.Equal(missing ? new[] { "authorize", "metadata", "open" } : ["authorize", "metadata", "open", "audit"], order);
    }
}
