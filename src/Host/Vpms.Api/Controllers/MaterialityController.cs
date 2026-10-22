using System.Security.Claims;
using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Vpms.Api.Auth;
using Vpms.Identity.Contracts;
using Vpms.Workflow.Application;
using Vpms.Workflow.Domain;

namespace Vpms.Api.Controllers;

public sealed record ProposeMaterialityRequest(MaterialityConfig Config, string Note);

/// <summary>Per-legal-entity settings for when a change to an approved invoice needs approval again. Every change needs a second person.</summary>
[ApiController, ApiVersion("1.0"), Route("api/v{version:apiVersion}")]
public sealed class MaterialityController(IMaterialityPolicyService policies, IPermissionEvaluator permissions) : ControllerBase
{
    [HttpGet("approval-materiality/fields")]
    public IActionResult Fields() => Ok(new { money = MaterialityConfig.MoneyFields, other = MaterialityConfig.TextFields });

    [HttpGet("admin/legal-entities/{legalEntityId:guid}/approval-materiality")]
    public async Task<IActionResult> Get(Guid legalEntityId, CancellationToken ct) =>
        await HasAny(legalEntityId, ct) ? Ok(await policies.GetAsync(legalEntityId, ct)) : Forbid();

    [HttpGet("admin/legal-entities/{legalEntityId:guid}/approval-materiality/history")]
    public async Task<IActionResult> History(Guid legalEntityId, CancellationToken ct) =>
        await HasAny(legalEntityId, ct) ? Ok(await policies.HistoryAsync(legalEntityId, ct)) : Forbid();

    [HttpPut("admin/legal-entities/{legalEntityId:guid}/approval-materiality")]
    public async Task<IActionResult> Propose(Guid legalEntityId, ProposeMaterialityRequest body, CancellationToken ct) =>
        await Has(Permissions.WorkflowConfigure, legalEntityId, ct) ? Ok(await policies.ProposeAsync(legalEntityId, body.Config, body.Note, ct)) : Forbid();

    [HttpGet("approval-materiality-changes")]
    public async Task<IActionResult> Changes([FromQuery] string? status, CancellationToken ct)
    {
        var (allowed, scope) = await this.ScopeForAsync(permissions, Permissions.WorkflowApproveChange, ct);
        return allowed ? Ok(await policies.ListChangesAsync(status, scope, ct)) : Forbid();
    }

    [HttpPost("approval-materiality-changes/{id:guid}/approve")]
    public async Task<IActionResult> Approve(Guid id, WorkflowChangeDecision body, CancellationToken ct) =>
        await Has(Permissions.WorkflowApproveChange, await policies.EntityOfChangeAsync(id, ct), ct) ? Ok(await policies.ApproveAsync(id, body.Comment, ct)) : NotFound();

    [HttpPost("approval-materiality-changes/{id:guid}/reject")]
    public async Task<IActionResult> Reject(Guid id, WorkflowChangeDecision body, CancellationToken ct) =>
        await Has(Permissions.WorkflowApproveChange, await policies.EntityOfChangeAsync(id, ct), ct) ? Ok(await policies.RejectAsync(id, body.Comment ?? "", ct)) : NotFound();

    [HttpPost("approval-materiality-changes/{id:guid}/cancel")]
    public async Task<IActionResult> Cancel(Guid id, CancellationToken ct) =>
        await Has(Permissions.WorkflowConfigure, await policies.EntityOfChangeAsync(id, ct), ct) ? Ok(await policies.CancelAsync(id, ct)) : NotFound();

    private Guid UserId => Guid.Parse(User.FindFirstValue(VpmsClaims.UserId)!);

    private Task<bool> Has(string permission, Guid legalEntityId, CancellationToken ct) => permissions.HasAsync(UserId, permission, legalEntityId, ct);

    private async Task<bool> HasAny(Guid legalEntityId, CancellationToken ct) =>
        await Has(Permissions.WorkflowView, legalEntityId, ct) || await Has(Permissions.WorkflowConfigure, legalEntityId, ct)
        || await Has(Permissions.WorkflowApproveChange, legalEntityId, ct);
}
