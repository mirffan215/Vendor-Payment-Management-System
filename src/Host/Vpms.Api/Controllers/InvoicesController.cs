using System.Security.Claims;
using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Vpms.Api.Auth;
using Vpms.BuildingBlocks.Security;
using Vpms.Documents.Contracts;
using Vpms.Identity.Contracts;
using Vpms.Invoices.Application;
using Vpms.BuildingBlocks.Errors;

namespace Vpms.Api.Controllers;

[ApiController, ApiVersion("1.0"), Route("api/v{version:apiVersion}/invoices")]
public sealed class InvoicesController(
    IInvoiceService invoices, IInvoiceAttachmentService invoiceFiles, IAttachmentService attachments, ICurrentUser current,
    IPermissionEvaluator permissions) : ControllerBase
{
    private const long MaxUpload = 25 * 1024 * 1024;

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string? status, [FromQuery] Guid? vendorId, [FromQuery] Guid? legalEntityId, [FromQuery] string? search,
        [FromQuery] string? source, [FromQuery] int skip = 0, [FromQuery] int take = 50, CancellationToken ct = default)
    {
        var (allowed, scope) = await this.ScopeForAsync(permissions, Permissions.InvoiceView, ct);
        return allowed
            ? Ok(await invoices.ListAsync(new InvoiceFilter(status, vendorId, legalEntityId, search, source), scope, skip, take, ct))
            : Forbid();
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) =>
        await Can(Permissions.InvoiceView, id, ct) ? Ok(await invoices.GetAsync(id, ct)) : NotFound();

    [HttpPost]
    public async Task<IActionResult> Create(CreateInvoice body, CancellationToken ct)
    {
        if (!await permissions.HasAsync(current.UserId, Permissions.InvoiceCreate, body.LegalEntityId, ct)) return Forbid();
        var created = await invoices.CreateAsync(body, ct);
        return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, InvoiceData body, CancellationToken ct) =>
        await Can(Permissions.InvoiceEdit, id, ct) ? Ok(await invoices.UpdateAsync(id, body, ct)) : NotFound();

    [HttpPost("{id:guid}/submit")]
    public async Task<IActionResult> Submit(Guid id, CancellationToken ct) =>
        await Can(Permissions.InvoiceEdit, id, ct) ? Ok(await invoices.SubmitAsync(id, ct)) : NotFound();

    [HttpPost("{id:guid}/cancel")]
    public async Task<IActionResult> Cancel(Guid id, ReasonRequest body, CancellationToken ct) =>
        await Can(Permissions.InvoiceEdit, id, ct) ? Ok(await invoices.CancelAsync(id, body.Reason, ct)) : NotFound();

    [HttpPost("{id:guid}/reject")]
    public async Task<IActionResult> Reject(Guid id, ReasonRequest body, CancellationToken ct) =>
        await Can(Permissions.InvoiceExceptionResolve, id, ct) ? Ok(await invoices.RejectAsync(id, body.Reason, ct)) : NotFound();

    [HttpPost("{id:guid}/attachments"), RequestSizeLimit(MaxUpload)]
    public async Task<IActionResult> AddAttachment(Guid id, [FromForm] string role, IFormFile file, CancellationToken ct)
    {
        if (!await Can(Permissions.InvoiceEdit, id, ct)) return NotFound();
        await using var stream = file.OpenReadStream();
        var entity = await invoices.EntityOfAsync(id, ct);
        var info = await attachments.UploadAsync(new UploadRequest("Invoice", id.ToString(), entity, file.FileName, stream, current.UserId, UploaderKind.Staff), ct);
        return Ok(await invoiceFiles.AddAsync(id, role, info.Id, ct));
    }

    [HttpGet("{id:guid}/attachments/{attachmentId:guid}/file")]
    public async Task<IActionResult> Download(Guid id, Guid attachmentId, CancellationToken ct)
    {
        if (!await Can(Permissions.InvoiceView, id, ct)) return NotFound();
        var file = await invoiceFiles.OpenAsync(id, attachmentId, ct);
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        return File(file.Content, file.ContentType, file.FileName);
    }

    // --- document channel and extraction suggestions -----------------------------------------------------------

    [HttpPost("from-document"), RequestSizeLimit(MaxUpload)]
    public async Task<IActionResult> FromDocument([FromForm] Guid legalEntityId, [FromForm] Guid vendorId, IFormFile file, [FromServices] IInvoiceIntakeService intake, CancellationToken ct)
    {
        if (!await permissions.HasAsync(current.UserId, Permissions.InvoiceCreate, legalEntityId, ct)) return Forbid();
        await using var stream = file.OpenReadStream();
        var created = await intake.CreateFromDocumentAsync(legalEntityId, vendorId, file.FileName, stream, ct);
        return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
    }

    [HttpGet("{id:guid}/extractions")]
    public async Task<IActionResult> Extractions(Guid id, [FromServices] IInvoiceExtractionService extraction, CancellationToken ct) =>
        await Can(Permissions.InvoiceView, id, ct) ? Ok(await extraction.ListAsync(id, ct)) : NotFound();

    [HttpPost("{id:guid}/extractions/{extractionId:guid}/dismiss")]
    public async Task<IActionResult> DismissExtraction(Guid id, Guid extractionId, [FromServices] IInvoiceExtractionService extraction, CancellationToken ct)
    {
        if (!await Can(Permissions.InvoiceEdit, id, ct)) return NotFound();
        if (!(await extraction.ListAsync(id, ct)).Any(e => e.Id == extractionId)) return NotFound();
        await extraction.DismissAsync(extractionId, ct);
        return NoContent();
    }

    /// <summary>Entity-scoped permission check on an existing invoice. Without it the invoice is reported as not found.</summary>
    private async Task<bool> Can(string permission, Guid invoiceId, CancellationToken ct) =>
        await permissions.HasAsync(Guid.Parse(User.FindFirstValue(VpmsClaims.UserId)!), permission, await invoices.EntityOfAsync(invoiceId, ct), ct);
}
