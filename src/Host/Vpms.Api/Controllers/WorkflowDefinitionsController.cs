using System.Security.Claims;
using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Vpms.Api.Auth;
using Vpms.Identity.Contracts;
using Vpms.Workflow.Application;
using Vpms.Workflow.Domain;

namespace Vpms.Api.Controllers;

public sealed record ProposeWorkflowRequest(WorkflowDefinitionSpec? Spec, bool FromMatrix, string Note);

/// <summary>Per-legal-entity approval workflow definitions. Every new version needs a second person's approval.</summary>
[ApiController, ApiVersion("1.0"), Route("api/v{version:apiVersion}")]
public sealed class WorkflowDefinitionsController(IWorkflowDefinitionService workflows, IPermissionEvaluator permissions) : ControllerBase
{
    [HttpGet("admin/legal-entities/{legalEntityId:guid}/workflow")]
    public async Task<IActionResult> Get(Guid legalEntityId, CancellationToken ct) =>
        await HasAny(legalEntityId, ct) ? Ok(await workflows.GetAsync(legalEntityId, ct)) : Forbid();

    [HttpGet("admin/legal-entities/{legalEntityId:guid}/workflow/history")]
    public async Task<IActionResult> History(Guid legalEntityId, CancellationToken ct) =>
        await HasAny(legalEntityId, ct) ? Ok(await workflows.HistoryAsync(legalEntityId, ct)) : Forbid();

    [HttpPut("admin/legal-entities/{legalEntityId:guid}/workflow")]
    public async Task<IActionResult> Propose(Guid legalEntityId, ProposeWorkflowRequest body, CancellationToken ct) =>
        await Has(Permissions.WorkflowConfigure, legalEntityId, ct)
            ? Ok(await workflows.ProposeAsync(legalEntityId, new ProposeDefinition(body.Spec, body.FromMatrix, body.Note), ct))
            : Forbid();

    /// <summary>The workflow the entity's approval matrix compiles to, without storing it.</summary>
    [HttpGet("admin/legal-entities/{legalEntityId:guid}/workflow/matrix-preview")]
    public async Task<IActionResult> MatrixPreview(Guid legalEntityId, CancellationToken ct) =>
        await Has(Permissions.WorkflowConfigure, legalEntityId, ct) ? Ok(await workflows.PreviewMatrixAsync(legalEntityId, ct)) : Forbid();

    [HttpPost("admin/legal-entities/{legalEntityId:guid}/workflow/validate")]
    public async Task<IActionResult> Validate(Guid legalEntityId, WorkflowDefinitionSpec spec, CancellationToken ct) =>
        await Has(Permissions.WorkflowConfigure, legalEntityId, ct) ? Ok(workflows.Validate(spec)) : Forbid();

    [HttpPost("admin/legal-entities/{legalEntityId:guid}/workflow/simulate")]
    public async Task<IActionResult> Simulate(Guid legalEntityId, SimulationRequest body, CancellationToken ct) =>
        await HasAny(legalEntityId, ct) ? Ok(await workflows.SimulateAsync(legalEntityId, body, ct)) : Forbid();

    [HttpGet("workflow-changes")]
    public async Task<IActionResult> Changes([FromQuery] string? status, CancellationToken ct)
    {
        var (allowed, scope) = await this.ScopeForAsync(permissions, Permissions.WorkflowApproveChange, ct);
        return allowed ? Ok(await workflows.ListChangesAsync(status, scope, ct)) : Forbid();
    }

    [HttpPost("workflow-changes/{id:guid}/approve")]
    public async Task<IActionResult> Approve(Guid id, WorkflowChangeDecision body, CancellationToken ct) =>
        await Has(Permissions.WorkflowApproveChange, await workflows.EntityOfChangeAsync(id, ct), ct) ? Ok(await workflows.ApproveAsync(id, body.Comment, ct)) : NotFound();

    [HttpPost("workflow-changes/{id:guid}/reject")]
    public async Task<IActionResult> Reject(Guid id, WorkflowChangeDecision body, CancellationToken ct) =>
        await Has(Permissions.WorkflowApproveChange, await workflows.EntityOfChangeAsync(id, ct), ct) ? Ok(await workflows.RejectAsync(id, body.Comment ?? "", ct)) : NotFound();

    [HttpPost("workflow-changes/{id:guid}/cancel")]
    public async Task<IActionResult> Cancel(Guid id, CancellationToken ct) =>
        await Has(Permissions.WorkflowConfigure, await workflows.EntityOfChangeAsync(id, ct), ct) ? Ok(await workflows.CancelAsync(id, ct)) : NotFound();

    private Guid UserId => Guid.Parse(User.FindFirstValue(VpmsClaims.UserId)!);

    private Task<bool> Has(string permission, Guid legalEntityId, CancellationToken ct) => permissions.HasAsync(UserId, permission, legalEntityId, ct);

    private async Task<bool> HasAny(Guid legalEntityId, CancellationToken ct) =>
        await Has(Permissions.WorkflowView, legalEntityId, ct) || await Has(Permissions.WorkflowConfigure, legalEntityId, ct)
        || await Has(Permissions.WorkflowApproveChange, legalEntityId, ct);
}

public sealed record WorkflowChangeDecision(string? Comment);
