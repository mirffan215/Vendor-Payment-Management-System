using System.Security.Claims;
using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Vpms.Api.Auth;
using Vpms.ErpIntegration.Application;
using Vpms.Identity.Contracts;

namespace Vpms.Api.Controllers;

public sealed record SyncRetryRequest(string Reason);

public sealed record SyncResolveRequest(string Reason, string? ErpReference);

/// <summary>The outbound ERP queue: what was sent, what failed, and the actions for a person to deal with dead-lettered messages.</summary>
[ApiController, ApiVersion("1.0"), Route("api/v{version:apiVersion}/erp/sync")]
public sealed class ErpSyncController(IErpSyncService sync, IPermissionEvaluator permissions) : ControllerBase
{
    [HttpGet("summary")]
    public async Task<IActionResult> Summary(CancellationToken ct)
    {
        var (allowed, scope) = await ScopeAsync(ct);
        return allowed ? Ok(await sync.SummaryAsync(scope, ct)) : Forbid();
    }

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string? status, [FromQuery] string? type, [FromQuery, System.ComponentModel.DataAnnotations.Range(0, 1_000_000)] int skip = 0, [FromQuery, System.ComponentModel.DataAnnotations.Range(1, 200)] int take = 50, CancellationToken ct = default)
    {
        var (allowed, scope) = await ScopeAsync(ct);
        return allowed ? Ok(await sync.ListAsync(status, type, scope, skip, take, ct)) : Forbid();
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) =>
        await CanView(await sync.EntityOfAsync(id, ct), ct) ? Ok(await sync.GetAsync(id, ct)) : NotFound();

    [HttpPost("{id:guid}/retry")]
    public async Task<IActionResult> Retry(Guid id, SyncRetryRequest body, CancellationToken ct) =>
        await Has(Permissions.ErpOperate, await sync.EntityOfAsync(id, ct), ct) ? Ok(await sync.RetryAsync(id, body.Reason, ct)) : NotFound();

    [HttpPost("{id:guid}/resolve")]
    public async Task<IActionResult> Resolve(Guid id, SyncResolveRequest body, CancellationToken ct) =>
        await Has(Permissions.ErpOperate, await sync.EntityOfAsync(id, ct), ct) ? Ok(await sync.ResolveAsync(id, body.Reason, body.ErpReference, ct)) : NotFound();

    private Guid UserId => Guid.Parse(User.FindFirstValue(VpmsClaims.UserId)!);

    private Task<bool> Has(string permission, Guid legalEntityId, CancellationToken ct) => permissions.HasAsync(UserId, permission, legalEntityId, ct);

    private async Task<bool> CanView(Guid legalEntityId, CancellationToken ct) => await Has(Permissions.ErpView, legalEntityId, ct) || await Has(Permissions.ErpOperate, legalEntityId, ct);

    private async Task<(bool Allowed, IReadOnlySet<Guid>? Scope)> ScopeAsync(CancellationToken ct)
    {
        var view = await this.ScopeForAsync(permissions, Permissions.ErpView, ct);
        var operate = await this.ScopeForAsync(permissions, Permissions.ErpOperate, ct);
        if (!view.Allowed && !operate.Allowed) return (false, null);
        if ((view.Allowed && view.Scope is null) || (operate.Allowed && operate.Scope is null)) return (true, null);
        return (true, (view.Scope ?? new HashSet<Guid>()).Union(operate.Scope ?? new HashSet<Guid>()).ToHashSet());
    }
}
