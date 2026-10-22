using System.Security.Claims;
using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Vpms.Api.Auth;
using Vpms.Identity.Contracts;
using Vpms.Invoices.Application;
using Vpms.Invoices.Domain.Verification;

namespace Vpms.Api.Controllers;

public sealed record ProposeRulesRequest(IReadOnlyDictionary<string, RuleSetting>? Rules, string Note);

public sealed record DecisionRequest(string? Comment);

/// <summary>Per-legal-entity verification settings. Loosening a rule needs a second person's approval.</summary>
[ApiController, ApiVersion("1.0"), Route("api/v{version:apiVersion}")]
public sealed class VerificationRulesController(IVerificationRuleSetService rules, IPermissionEvaluator permissions) : ControllerBase
{
    /// <summary>What can be configured: the rules, their default switches and severities, and thresholds with limits.</summary>
    [HttpGet("verification-rules/catalog")]
    public IActionResult Catalog() => Ok(RuleCatalog.All);

    [HttpGet("admin/legal-entities/{legalEntityId:guid}/verification-rules")]
    public async Task<IActionResult> Get(Guid legalEntityId, CancellationToken ct) =>
        await HasAny(legalEntityId, ct) ? Ok(await rules.GetAsync(legalEntityId, ct)) : Forbid();

    [HttpGet("admin/legal-entities/{legalEntityId:guid}/verification-rules/history")]
    public async Task<IActionResult> History(Guid legalEntityId, CancellationToken ct) =>
        await HasAny(legalEntityId, ct) ? Ok(await rules.HistoryAsync(legalEntityId, ct)) : Forbid();

    [HttpPut("admin/legal-entities/{legalEntityId:guid}/verification-rules")]
    public async Task<IActionResult> Propose(Guid legalEntityId, ProposeRulesRequest body, CancellationToken ct) =>
        await Has(Permissions.VerificationConfigure, legalEntityId, ct)
            ? Ok(await rules.ProposeAsync(legalEntityId, new ProposeRuleChange(body.Rules ?? new Dictionary<string, RuleSetting>(), body.Note), ct))
            : Forbid();

    [HttpGet("verification-rule-changes")]
    public async Task<IActionResult> Changes([FromQuery] string? status, CancellationToken ct)
    {
        var (allowed, scope) = await this.ScopeForAsync(permissions, Permissions.VerificationApproveChange, ct);
        return allowed ? Ok(await rules.ListChangesAsync(status, scope, ct)) : Forbid();
    }

    [HttpPost("verification-rule-changes/{id:guid}/approve")]
    public async Task<IActionResult> Approve(Guid id, DecisionRequest body, CancellationToken ct) =>
        await Has(Permissions.VerificationApproveChange, await rules.EntityOfChangeAsync(id, ct), ct) ? Ok(await rules.ApproveAsync(id, body.Comment, ct)) : NotFound();

    [HttpPost("verification-rule-changes/{id:guid}/reject")]
    public async Task<IActionResult> Reject(Guid id, DecisionRequest body, CancellationToken ct) =>
        await Has(Permissions.VerificationApproveChange, await rules.EntityOfChangeAsync(id, ct), ct) ? Ok(await rules.RejectAsync(id, body.Comment ?? "", ct)) : NotFound();

    [HttpPost("verification-rule-changes/{id:guid}/cancel")]
    public async Task<IActionResult> Cancel(Guid id, CancellationToken ct) =>
        await Has(Permissions.VerificationConfigure, await rules.EntityOfChangeAsync(id, ct), ct) ? Ok(await rules.CancelAsync(id, ct)) : NotFound();

    private Guid UserId => Guid.Parse(User.FindFirstValue(VpmsClaims.UserId)!);

    private Task<bool> Has(string permission, Guid legalEntityId, CancellationToken ct) => permissions.HasAsync(UserId, permission, legalEntityId, ct);

    private async Task<bool> HasAny(Guid legalEntityId, CancellationToken ct) =>
        await Has(Permissions.VerificationConfigure, legalEntityId, ct) || await Has(Permissions.VerificationApproveChange, legalEntityId, ct);
}
