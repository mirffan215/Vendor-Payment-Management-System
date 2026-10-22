using System.Security.Claims;
using System.Text;
using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Vpms.Api.Auth;
using Vpms.ErpIntegration.Application;
using Vpms.Identity.Contracts;

namespace Vpms.Api.Controllers;

public sealed record ReconRunRequest(Guid LegalEntityId);

public sealed record ReconReasonRequest(string Reason);

public sealed record ReconCommentRequest(string? Comment);

/// <summary>
/// Reconciliation of VPMS with the ERP: run it, read the report, and account for each difference. Accepting a money or payee difference
/// is proposed by one person and confirmed by a different one.
/// </summary>
[ApiController, ApiVersion("1.0"), Route("api/v{version:apiVersion}/erp/reconciliation")]
public sealed class ErpReconciliationController(IReconciliationService reconciliation, IPermissionEvaluator permissions) : ControllerBase
{
    [HttpPost("run")]
    public async Task<IActionResult> Run(ReconRunRequest body, CancellationToken ct) =>
        await Has(Permissions.ErpOperate, body.LegalEntityId, ct) ? Ok(await reconciliation.RunAsync(body.LegalEntityId, ct)) : Forbid();

    [HttpGet("runs")]
    public async Task<IActionResult> Runs([FromQuery] Guid? legalEntityId, [FromQuery] int take = 20, CancellationToken ct = default)
    {
        var (allowed, scope) = await ScopeAsync(ct);
        return allowed ? Ok(await reconciliation.RunsAsync(legalEntityId, scope, take, ct)) : Forbid();
    }

    [HttpGet("summary")]
    public async Task<IActionResult> Summary(CancellationToken ct)
    {
        var (allowed, scope) = await ScopeAsync(ct);
        return allowed ? Ok(await reconciliation.SummaryAsync(scope, ct)) : Forbid();
    }

    [HttpGet("items")]
    public async Task<IActionResult> Items([FromQuery] Guid? legalEntityId, [FromQuery] string? status, [FromQuery] string? type, [FromQuery] int skip = 0, [FromQuery] int take = 100, CancellationToken ct = default)
    {
        var (allowed, scope) = await ScopeAsync(ct);
        return allowed ? Ok(await reconciliation.ItemsAsync(legalEntityId, status, type, scope, skip, take, ct)) : Forbid();
    }

    [HttpGet("export")]
    public async Task<IActionResult> Export([FromQuery] Guid? legalEntityId, [FromQuery] string? status, CancellationToken ct = default)
    {
        var (allowed, scope) = await ScopeAsync(ct);
        if (!allowed) return Forbid();
        var csv = await reconciliation.ExportCsvAsync(legalEntityId, status, scope, ct);
        return File(Encoding.UTF8.GetBytes(csv), "text/csv; charset=utf-8", "erp-reconciliation.csv");
    }

    [HttpPost("items/{id:guid}/resolve")]
    public async Task<IActionResult> Resolve(Guid id, ReconReasonRequest body, CancellationToken ct) =>
        await Has(Permissions.ErpOperate, await reconciliation.EntityOfItemAsync(id, ct), ct) ? Ok(await reconciliation.ResolveAsync(id, body.Reason, ct)) : NotFound();

    [HttpPost("items/{id:guid}/propose-acceptance")]
    public async Task<IActionResult> Propose(Guid id, ReconReasonRequest body, CancellationToken ct) =>
        await Has(Permissions.ErpOperate, await reconciliation.EntityOfItemAsync(id, ct), ct) ? Ok(await reconciliation.ProposeAcceptanceAsync(id, body.Reason, ct)) : NotFound();

    [HttpPost("items/{id:guid}/approve-acceptance")]
    public async Task<IActionResult> Approve(Guid id, ReconCommentRequest body, CancellationToken ct) =>
        await Has(Permissions.ErpApproveChange, await reconciliation.EntityOfItemAsync(id, ct), ct) ? Ok(await reconciliation.ApproveAcceptanceAsync(id, body.Comment, ct)) : NotFound();

    [HttpPost("items/{id:guid}/reject-acceptance")]
    public async Task<IActionResult> Reject(Guid id, ReconReasonRequest body, CancellationToken ct) =>
        await Has(Permissions.ErpApproveChange, await reconciliation.EntityOfItemAsync(id, ct), ct) ? Ok(await reconciliation.RejectAcceptanceAsync(id, body.Reason, ct)) : NotFound();

    private Guid UserId => Guid.Parse(User.FindFirstValue(VpmsClaims.UserId)!);

    private Task<bool> Has(string permission, Guid legalEntityId, CancellationToken ct) => permissions.HasAsync(UserId, permission, legalEntityId, ct);

    private async Task<(bool Allowed, IReadOnlySet<Guid>? Scope)> ScopeAsync(CancellationToken ct)
    {
        var scopes = new List<(bool Allowed, IReadOnlySet<Guid>? Scope)>();
        foreach (var permission in new[] { Permissions.ErpView, Permissions.ErpOperate, Permissions.ErpApproveChange }) scopes.Add(await this.ScopeForAsync(permissions, permission, ct));
        var allowed = scopes.Where(s => s.Allowed).ToList();
        if (allowed.Count == 0) return (false, null);
        if (allowed.Any(s => s.Scope is null)) return (true, null);
        return (true, allowed.SelectMany(s => s.Scope!).ToHashSet());
    }
}
