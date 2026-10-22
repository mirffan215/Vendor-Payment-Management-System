using System.Security.Claims;
using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Vpms.Api.Auth;
using Vpms.Identity.Contracts;
using Vpms.Payments.Application;

namespace Vpms.Api.Controllers;

public sealed record CreateRequestsBody(IReadOnlyList<Guid> InvoiceIds);

public sealed record PaymentReasonBody(string Reason);

/// <summary>Payment requests: the intention to pay an approved invoice. Nothing here moves money.</summary>
[ApiController, ApiVersion("1.0"), Route("api/v{version:apiVersion}/payments")]
public sealed class PaymentRequestsController(IPaymentRequestService requests, IPermissionEvaluator permissions) : ControllerBase
{
    [HttpGet("payable")]
    public async Task<IActionResult> Payable([FromQuery] Guid? legalEntityId, CancellationToken ct)
    {
        var (allowed, scope) = await this.ScopeForAsync(permissions, Permissions.PaymentPrepare, ct);
        return allowed ? Ok(await requests.ListPayableAsync(legalEntityId, scope, ct)) : Forbid();
    }

    [HttpGet("requests")]
    public async Task<IActionResult> List([FromQuery] string? status, [FromQuery] Guid? legalEntityId, [FromQuery] Guid? vendorId, [FromQuery, System.ComponentModel.DataAnnotations.Range(0, 1_000_000)] int skip = 0, [FromQuery, System.ComponentModel.DataAnnotations.Range(1, 200)] int take = 50, CancellationToken ct = default)
    {
        var (allowed, scope) = await this.ScopeForAsync(permissions, Permissions.PaymentView, ct);
        return allowed ? Ok(await requests.ListAsync(new PaymentRequestFilter(status, legalEntityId, vendorId), scope, skip, take, ct)) : Forbid();
    }

    [HttpPost("requests")]
    public async Task<IActionResult> Create(CreateRequestsBody body, CancellationToken ct)
    {
        foreach (var id in body.InvoiceIds.Distinct())
        {
            if (!await Has(Permissions.PaymentPrepare, await requests.EntityOfInvoiceAsync(id, ct), ct)) return NotFound();
        }

        return Ok(await requests.CreateAsync(body.InvoiceIds, ct));
    }

    [HttpPost("requests/{id:guid}/cancel")]
    public async Task<IActionResult> Cancel(Guid id, PaymentReasonBody body, CancellationToken ct) =>
        await Has(Permissions.PaymentPrepare, await requests.EntityOfAsync(id, ct), ct) ? Ok(await requests.CancelAsync(id, body.Reason, ct)) : NotFound();

    private Guid UserId => Guid.Parse(User.FindFirstValue(VpmsClaims.UserId)!);

    private Task<bool> Has(string permission, Guid legalEntityId, CancellationToken ct) => permissions.HasAsync(UserId, permission, legalEntityId, ct);
}
