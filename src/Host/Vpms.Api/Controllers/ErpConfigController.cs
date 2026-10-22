using System.Security.Claims;
using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Vpms.Api.Auth;
using Vpms.ErpIntegration.Application;
using Vpms.ErpIntegration.Domain;
using Vpms.Identity.Contracts;

namespace Vpms.Api.Controllers;

public sealed record ProposeConnectionRequest(ConnectionConfig Config, string Note);

public sealed record ProposeMappingRequest(MappingConfig Config, string Note);

public sealed record ErpChangeDecision(string? Comment);

/// <summary>
/// ERP connection (tenant-wide) and per-legal-entity mapping. Every version needs a second person's approval. The API never returns a
/// secret: a connection holds only the name of the secret in the vault.
/// </summary>
[ApiController, ApiVersion("1.0"), Route("api/v{version:apiVersion}")]
public sealed class ErpConfigController(IErpConfigService erp, IPermissionEvaluator permissions) : ControllerBase
{
    [HttpGet("admin/erp/connection")]
    public async Task<IActionResult> GetConnection(CancellationToken ct) =>
        await HasAny(null, ct) ? Ok(await erp.GetConnectionAsync(ct)) : Forbid();

    [HttpPut("admin/erp/connection")]
    public async Task<IActionResult> ProposeConnection(ProposeConnectionRequest body, CancellationToken ct) =>
        await Has(Permissions.ErpConfigure, null, ct) ? Ok(await erp.ProposeConnectionAsync(body.Config, body.Note, ct)) : Forbid();

    [HttpGet("admin/legal-entities/{legalEntityId:guid}/erp-mapping")]
    public async Task<IActionResult> GetMapping(Guid legalEntityId, CancellationToken ct) =>
        await HasAny(legalEntityId, ct) ? Ok(await erp.GetMappingAsync(legalEntityId, ct)) : Forbid();

    [HttpPut("admin/legal-entities/{legalEntityId:guid}/erp-mapping")]
    public async Task<IActionResult> ProposeMapping(Guid legalEntityId, ProposeMappingRequest body, CancellationToken ct) =>
        await Has(Permissions.ErpConfigure, legalEntityId, ct) ? Ok(await erp.ProposeMappingAsync(legalEntityId, body.Config, body.Note, ct)) : Forbid();

    [HttpPost("admin/legal-entities/{legalEntityId:guid}/erp/test")]
    public async Task<IActionResult> Test(Guid legalEntityId, CancellationToken ct) =>
        await Has(Permissions.ErpConfigure, legalEntityId, ct) || await Has(Permissions.ErpOperate, legalEntityId, ct)
            ? Ok(await erp.TestAsync(legalEntityId, ct))
            : Forbid();

    [HttpGet("erp-changes")]
    public async Task<IActionResult> Changes([FromQuery] string? status, CancellationToken ct)
    {
        var (allowed, scope) = await this.ScopeForAsync(permissions, Permissions.ErpApproveChange, ct);
        if (!allowed) return Forbid();
        var tenantWide = await Has(Permissions.ErpApproveChange, null, ct);
        return Ok(await erp.ListChangesAsync(status, scope, tenantWide, ct));
    }

    [HttpPost("erp-changes/{id:guid}/approve")]
    [Vpms.Api.Auth.RequireStepUp]
    public async Task<IActionResult> Approve(Guid id, ErpChangeDecision body, CancellationToken ct) =>
        await Has(Permissions.ErpApproveChange, await erp.EntityOfChangeAsync(id, ct), ct) ? Ok(await erp.ApproveAsync(id, body.Comment, ct)) : NotFound();

    [HttpPost("erp-changes/{id:guid}/reject")]
    public async Task<IActionResult> Reject(Guid id, ErpChangeDecision body, CancellationToken ct) =>
        await Has(Permissions.ErpApproveChange, await erp.EntityOfChangeAsync(id, ct), ct) ? Ok(await erp.RejectAsync(id, body.Comment ?? "", ct)) : NotFound();

    [HttpPost("erp-changes/{id:guid}/cancel")]
    public async Task<IActionResult> Cancel(Guid id, CancellationToken ct) =>
        await Has(Permissions.ErpConfigure, await erp.EntityOfChangeAsync(id, ct), ct) ? Ok(await erp.CancelAsync(id, ct)) : NotFound();

    private Guid UserId => Guid.Parse(User.FindFirstValue(VpmsClaims.UserId)!);

    private Task<bool> Has(string permission, Guid? legalEntityId, CancellationToken ct) => permissions.HasAsync(UserId, permission, legalEntityId, ct);

    private async Task<bool> HasAny(Guid? legalEntityId, CancellationToken ct) =>
        await Has(Permissions.ErpView, legalEntityId, ct) || await Has(Permissions.ErpConfigure, legalEntityId, ct) || await Has(Permissions.ErpApproveChange, legalEntityId, ct);
}
