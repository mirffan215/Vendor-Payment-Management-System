using System.Security.Claims;
using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Vpms.Api.Auth;
using Vpms.Identity.Contracts;
using Vpms.Invoices.Application;

namespace Vpms.Api.Controllers;

public sealed record AssignMessageRequest(Guid VendorId);

public sealed record SaveMailboxRequest(string Address, bool Enabled);

/// <summary>Email intake administration: unmatched mail queue and the mailbox of each legal entity.</summary>
[ApiController, ApiVersion("1.0"), Route("api/v{version:apiVersion}")]
public sealed class InvoiceIntakeController(IInvoiceIntakeService intake, IPermissionEvaluator permissions) : ControllerBase
{
    [HttpGet("invoice-intake/messages")]
    public async Task<IActionResult> Messages([FromQuery] string? status, [FromQuery, System.ComponentModel.DataAnnotations.Range(0, 1_000_000)] int skip = 0, [FromQuery, System.ComponentModel.DataAnnotations.Range(1, 200)] int take = 50, CancellationToken ct = default)
    {
        var (allowed, scope) = await this.ScopeForAsync(permissions, Permissions.InvoiceIntakeManage, ct);
        return allowed ? Ok(await intake.ListMessagesAsync(status, scope, skip, take, ct)) : Forbid();
    }

    [HttpPost("invoice-intake/messages/{id:guid}/assign")]
    public async Task<IActionResult> Assign(Guid id, AssignMessageRequest body, CancellationToken ct) =>
        await CanMessage(id, ct) ? Ok(await intake.AssignAsync(id, body.VendorId, ct)) : NotFound();

    [HttpPost("invoice-intake/messages/{id:guid}/dismiss")]
    public async Task<IActionResult> Dismiss(Guid id, ReasonRequest body, CancellationToken ct) =>
        await CanMessage(id, ct) ? Ok(await intake.DismissAsync(id, body.Reason, ct)) : NotFound();

    [HttpGet("admin/legal-entities/{legalEntityId:guid}/invoice-mailbox")]
    public async Task<IActionResult> GetMailbox(Guid legalEntityId, CancellationToken ct) =>
        await Has(legalEntityId, ct) ? Ok(await intake.GetMailboxAsync(legalEntityId, ct)) : Forbid();

    [HttpPut("admin/legal-entities/{legalEntityId:guid}/invoice-mailbox")]
    public async Task<IActionResult> SaveMailbox(Guid legalEntityId, SaveMailboxRequest body, CancellationToken ct) =>
        await Has(legalEntityId, ct) ? Ok(await intake.SaveMailboxAsync(legalEntityId, body.Address, body.Enabled, ct)) : Forbid();

    private async Task<bool> CanMessage(Guid messageId, CancellationToken ct) => await Has(await intake.EntityOfMessageAsync(messageId, ct), ct);

    private Task<bool> Has(Guid legalEntityId, CancellationToken ct) =>
        permissions.HasAsync(Guid.Parse(User.FindFirstValue(VpmsClaims.UserId)!), Permissions.InvoiceIntakeManage, legalEntityId, ct);
}
