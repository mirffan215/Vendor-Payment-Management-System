using System.Security.Claims;
using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Vpms.Api.Auth;
using Vpms.Identity.Contracts;
using Vpms.Workflow.Application;

namespace Vpms.Api.Controllers;

/// <summary>Substitute approvers, and the in-app reminders and escalation notices the approval deadlines produce.</summary>
[ApiController, ApiVersion("1.0"), Route("api/v{version:apiVersion}")]
public sealed class DelegationsController(IDelegationService delegations, IWorkflowTimers timers, IPermissionEvaluator permissions) : ControllerBase
{
    [HttpGet("delegations")]
    public async Task<IActionResult> Mine([FromQuery] bool activeOnly, CancellationToken ct)
    {
        var (allowed, _) = await this.ScopeForAsync(permissions, Permissions.InvoiceApprove, ct);
        return allowed ? Ok(await delegations.MineAsync(activeOnly, ct)) : Forbid();
    }

    /// <summary>People who can approve for a legal entity, for choosing a delegate or a workflow approver.</summary>
    [HttpGet("approvals/approvers")]
    public async Task<IActionResult> Approvers([FromQuery] Guid legalEntityId, CancellationToken ct) =>
        await Has(Permissions.InvoiceApprove, legalEntityId, ct) || await Has(Permissions.WorkflowConfigure, legalEntityId, ct) || await Has(Permissions.WorkflowAdminister, legalEntityId, ct)
            ? Ok(await delegations.ApproversAsync(legalEntityId, ct))
            : Forbid();

    [HttpPost("delegations")]
    public async Task<IActionResult> Create(CreateDelegation body, CancellationToken ct)
    {
        var admin = await Has(Permissions.WorkflowAdminister, body.LegalEntityId, ct);
        if (!admin && !await Has(Permissions.InvoiceApprove, body.LegalEntityId, ct)) return Forbid();
        return Ok(await delegations.CreateAsync(body, admin, ct));
    }

    [HttpPost("delegations/{id:guid}/revoke")]
    public async Task<IActionResult> Revoke(Guid id, CancellationToken ct)
    {
        var admin = await Has(Permissions.WorkflowAdminister, await delegations.EntityOfAsync(id, ct), ct);
        return Ok(await delegations.RevokeAsync(id, admin, ct));
    }

    [HttpGet("admin/legal-entities/{legalEntityId:guid}/delegations")]
    public async Task<IActionResult> ForEntity(Guid legalEntityId, CancellationToken ct) =>
        await Has(Permissions.WorkflowAdminister, legalEntityId, ct) ? Ok(await delegations.ForEntityAsync(legalEntityId, ct)) : Forbid();

    [HttpGet("approvals/notices")]
    public async Task<IActionResult> Notices([FromQuery] bool unreadOnly, CancellationToken ct)
    {
        var (allowed, _) = await this.ScopeForAsync(permissions, Permissions.InvoiceApprove, ct);
        return allowed ? Ok(await timers.NoticesAsync(unreadOnly, ct)) : Forbid();
    }

    [HttpPost("approvals/notices/{id:guid}/read")]
    public async Task<IActionResult> Read(Guid id, CancellationToken ct)
    {
        await timers.MarkNoticeReadAsync(id, ct);
        return NoContent();
    }

    private Guid UserId => Guid.Parse(User.FindFirstValue(VpmsClaims.UserId)!);

    private Task<bool> Has(string permission, Guid legalEntityId, CancellationToken ct) => permissions.HasAsync(UserId, permission, legalEntityId, ct);
}
