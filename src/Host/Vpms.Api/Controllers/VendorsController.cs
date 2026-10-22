using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Vpms.Api.Auth;
using Vpms.BuildingBlocks.Security;
using Vpms.Documents.Contracts;
using Vpms.Identity.Contracts;
using Vpms.Vendors.Application;

namespace Vpms.Api.Controllers;

public sealed record ReasonRequest(string Reason);

public sealed record MessageRequest(string Subject, string Body);

public sealed record BodyRequest(string Body);

[ApiController, ApiVersion("1.0"), Route("api/v{version:apiVersion}/vendors")]
public sealed class VendorsController(
    IVendorService vendors, IVendorDocumentService documents, IVendorEntityLinkService links, IVendorRequestService requests,
    IAttachmentService attachments, ICurrentUser current, IPermissionEvaluator permissions) : ControllerBase
{
    private const long MaxUpload = 25 * 1024 * 1024;

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string? status, [FromQuery] string? search, [FromQuery] int skip = 0, [FromQuery] int take = 50, CancellationToken ct = default)
    {
        var (allowed, scope) = await this.ScopeForAsync(permissions, Permissions.VendorView, ct);
        return allowed ? Ok(await vendors.ListAsync(status, search, scope, skip, take, ct)) : Forbid();
    }

    [HttpGet("{id:guid}"), Authorize(Policy = Permissions.VendorView)]
    public async Task<VendorDetails> Get(Guid id, CancellationToken ct) => await vendors.GetAsync(id, ct);

    [HttpPost, Authorize(Policy = Permissions.VendorEdit)]
    public async Task<IActionResult> Create(CreateVendor body, CancellationToken ct)
    {
        var created = await vendors.CreateAsync(body, ct);
        return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
    }

    [HttpPut("{id:guid}"), Authorize(Policy = Permissions.VendorEdit)]
    public async Task<VendorDto> Update(Guid id, UpdateVendor body, CancellationToken ct) => await vendors.UpdateAsync(id, body, ct);

    [HttpPost("{id:guid}/submit"), Authorize(Policy = Permissions.VendorEdit)]
    public async Task<VendorDto> Submit(Guid id, CancellationToken ct) => await vendors.SubmitAsync(id, ct);

    [HttpPost("{id:guid}/reopen"), Authorize(Policy = Permissions.VendorEdit)]
    public async Task<VendorDto> Reopen(Guid id, CancellationToken ct) => await vendors.ReopenAsync(id, ct);

    [HttpPost("{id:guid}/start-review"), Authorize(Policy = Permissions.VendorApprove)]
    public async Task<VendorDto> StartReview(Guid id, CancellationToken ct) => await vendors.StartReviewAsync(id, ct);

    [HttpPost("{id:guid}/approve"), Authorize(Policy = Permissions.VendorApprove)]
    public async Task<VendorDto> Approve(Guid id, CancellationToken ct) => await vendors.ApproveAsync(id, ct);

    [HttpPost("{id:guid}/reject"), Authorize(Policy = Permissions.VendorApprove)]
    public async Task<VendorDto> Reject(Guid id, ReasonRequest body, CancellationToken ct) => await vendors.RejectAsync(id, body.Reason, ct);

    [HttpPost("{id:guid}/suspend"), Authorize(Policy = Permissions.VendorApprove)]
    public async Task<VendorDto> Suspend(Guid id, ReasonRequest body, CancellationToken ct) => await vendors.SuspendAsync(id, body.Reason, ct);

    [HttpPost("{id:guid}/reactivate"), Authorize(Policy = Permissions.VendorApprove)]
    public async Task<VendorDto> Reactivate(Guid id, CancellationToken ct) => await vendors.ReactivateAsync(id, ct);

    // --- documents -----------------------------------------------------------------------------------------

    [HttpGet("{id:guid}/documents"), Authorize(Policy = Permissions.VendorView)]
    public async Task<IReadOnlyList<VendorDocumentDto>> Documents(Guid id, CancellationToken ct) => await documents.ListAsync(id, ct);

    [HttpPost("{id:guid}/documents"), Authorize(Policy = Permissions.VendorEdit), RequestSizeLimit(MaxUpload), Microsoft.AspNetCore.RateLimiting.EnableRateLimiting(Vpms.Api.Infrastructure.HostLimits.UploadPolicy)]
    public async Task<VendorDocumentDto> AddDocument(Guid id, [FromForm] string type, [FromForm] DateOnly? expiresOn, IFormFile file, CancellationToken ct)
    {
        await using var stream = file.OpenReadStream();
        var info = await attachments.UploadAsync(new UploadRequest("Vendor", id.ToString(), null, file.FileName, stream, current.UserId, UploaderKind.Staff), ct);
        return await documents.AddAsync(id, type, info.Id, expiresOn, ct);
    }

    [HttpGet("{id:guid}/documents/{documentId:guid}/file"), Authorize(Policy = Permissions.VendorView)]
    public async Task<IActionResult> Download(Guid id, Guid documentId, CancellationToken ct)
    {
        var doc = (await documents.ListAsync(id, ct)).FirstOrDefault(d => d.Id == documentId);
        if (doc is null) return NotFound();
        var file = await attachments.OpenDownloadAsync(doc.AttachmentId, ct);
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        return File(file.Content, file.ContentType, file.FileName);
    }

    [HttpPost("{id:guid}/documents/{documentId:guid}/verify"), Authorize(Policy = Permissions.VendorApprove)]
    public async Task<VendorDocumentDto> Verify(Guid id, Guid documentId, CancellationToken ct) => await documents.VerifyAsync(documentId, ct);

    [HttpPost("{id:guid}/documents/{documentId:guid}/reject"), Authorize(Policy = Permissions.VendorApprove)]
    public async Task<VendorDocumentDto> RejectDocument(Guid id, Guid documentId, ReasonRequest body, CancellationToken ct) =>
        await documents.RejectAsync(documentId, body.Reason, ct);

    // --- legal-entity relationships (entity-scoped permission through the route) ---------------------------

    [HttpGet("{id:guid}/entities")]
    public async Task<IActionResult> Links(Guid id, CancellationToken ct)
    {
        var (allowed, scope) = await this.ScopeForAsync(permissions, Permissions.VendorView, ct);
        return allowed ? Ok(await links.ListAsync(id, scope, ct)) : Forbid();
    }

    [HttpPut("{id:guid}/entities/{legalEntityId:guid}"), Authorize(Policy = Permissions.VendorEdit)]
    public async Task<VendorEntityLinkDto> SaveLink(Guid id, Guid legalEntityId, SaveVendorEntityLink body, CancellationToken ct) =>
        await links.SaveAsync(id, legalEntityId, body, ct);

    [HttpPost("{id:guid}/entities/{legalEntityId:guid}/block"), Authorize(Policy = Permissions.VendorEdit)]
    public async Task<VendorEntityLinkDto> Block(Guid id, Guid legalEntityId, CancellationToken ct) => await links.SetBlockedAsync(id, legalEntityId, true, ct);

    [HttpPost("{id:guid}/entities/{legalEntityId:guid}/unblock"), Authorize(Policy = Permissions.VendorEdit)]
    public async Task<VendorEntityLinkDto> Unblock(Guid id, Guid legalEntityId, CancellationToken ct) => await links.SetBlockedAsync(id, legalEntityId, false, ct);

    // --- requests and messages -----------------------------------------------------------------------------

    [HttpGet("{id:guid}/requests"), Authorize(Policy = Permissions.VendorView)]
    public async Task<IReadOnlyList<RequestDto>> Requests(Guid id, CancellationToken ct) => await requests.ListAsync(id, ct);

    [HttpPost("{id:guid}/requests"), Authorize(Policy = Permissions.VendorEdit)]
    public async Task<RequestDto> CreateRequest(Guid id, MessageRequest body, CancellationToken ct) => await requests.CreateAsync(id, body.Subject, body.Body, ct);

    [HttpPost("{id:guid}/requests/{requestId:guid}/reply"), Authorize(Policy = Permissions.VendorEdit)]
    public async Task<RequestDto> Reply(Guid id, Guid requestId, BodyRequest body, CancellationToken ct) => await requests.ReplyAsync(requestId, body.Body, ct);

    [HttpPost("{id:guid}/requests/{requestId:guid}/close"), Authorize(Policy = Permissions.VendorEdit)]
    public async Task<RequestDto> CloseRequest(Guid id, Guid requestId, CancellationToken ct) => await requests.CloseAsync(requestId, ct);
}
