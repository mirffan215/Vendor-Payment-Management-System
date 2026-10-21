using System.Security.Claims;
using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Vpms.Api.Auth;
using Vpms.Identity.Contracts;
using Vpms.Invoices.Application;

namespace Vpms.Api.Controllers;

public sealed record AssignExceptionRequest(Guid? UserId);

/// <summary>The exceptions queue: failed checks waiting for a person.</summary>
[ApiController, ApiVersion("1.0"), Route("api/v{version:apiVersion}/invoice-exceptions")]
public sealed class InvoiceExceptionsController(IInvoiceExceptionService exceptions, IPermissionEvaluator permissions) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string? status, [FromQuery] Guid? assignedTo, [FromQuery] int skip = 0, [FromQuery] int take = 50, CancellationToken ct = default)
    {
        var (allowed, scope) = await this.ScopeForAsync(permissions, Permissions.InvoiceExceptionResolve, ct);
        if (!allowed)
        {
            // Reviewers who can only override still need to see the queue.
            (allowed, scope) = await this.ScopeForAsync(permissions, Permissions.InvoiceExceptionOverride, ct);
        }

        return allowed ? Ok(await exceptions.ListAsync(status, assignedTo, scope, skip, take, ct)) : Forbid();
    }

    /// <summary>Flags need the resolve permission; blocking checks need the override permission and a different person than the invoice's maker.</summary>
    [HttpPost("{id:guid}/waive")]
    public async Task<IActionResult> Waive(Guid id, ReasonRequest body, CancellationToken ct) =>
        await CanDecide(id, ct) ? Ok(await exceptions.WaiveAsync(id, body.Reason, ct)) : NotFound();

    [HttpPost("{id:guid}/assign")]
    public async Task<IActionResult> Assign(Guid id, AssignExceptionRequest body, CancellationToken ct) =>
        await CanDecide(id, ct) ? Ok(await exceptions.AssignAsync(id, body.UserId, ct)) : NotFound();

    private async Task<bool> CanDecide(Guid exceptionId, CancellationToken ct)
    {
        var user = Guid.Parse(User.FindFirstValue(VpmsClaims.UserId)!);
        var entity = await exceptions.EntityOfAsync(exceptionId, ct);
        return await permissions.HasAsync(user, Permissions.InvoiceExceptionResolve, entity, ct) || await permissions.HasAsync(user, Permissions.InvoiceExceptionOverride, entity, ct);
    }
}
