using System.Security.Claims;
using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Vpms.Api.Auth;
using Vpms.Identity.Contracts;
using Vpms.Invoices.Application;

namespace Vpms.Api.Controllers;

public sealed record ImportPurchaseOrdersRequest(IReadOnlyList<PoImportItem> PurchaseOrders);

/// <summary>Purchase orders used by PO matching. Imported by staff until the ERP integration (Phase 6) supplies them.</summary>
[ApiController, ApiVersion("1.0"), Route("api/v{version:apiVersion}")]
public sealed class PurchaseOrdersController(IPurchaseOrderService orders, IPermissionEvaluator permissions) : ControllerBase
{
    [HttpPost("admin/legal-entities/{legalEntityId:guid}/purchase-orders/import")]
    public async Task<IActionResult> Import(Guid legalEntityId, ImportPurchaseOrdersRequest body, CancellationToken ct) =>
        await permissions.HasAsync(UserId, Permissions.InvoicePoImport, legalEntityId, ct)
            ? Ok(await orders.ImportAsync(legalEntityId, body.PurchaseOrders ?? [], ct))
            : Forbid();

    [HttpGet("purchase-orders")]
    public async Task<IActionResult> List([FromQuery] Guid? legalEntityId, [FromQuery] string? search, [FromQuery] int skip = 0, [FromQuery] int take = 50, CancellationToken ct = default)
    {
        var (allowed, scope) = await this.ScopeForAsync(permissions, Permissions.InvoiceView, ct);
        return allowed ? Ok(await orders.ListAsync(legalEntityId, search, scope, skip, take, ct)) : Forbid();
    }

    [HttpGet("purchase-orders/{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) =>
        await permissions.HasAsync(UserId, Permissions.InvoiceView, await orders.EntityOfAsync(id, ct), ct) ? Ok(await orders.GetAsync(id, ct)) : NotFound();

    private Guid UserId => Guid.Parse(User.FindFirstValue(VpmsClaims.UserId)!);
}
