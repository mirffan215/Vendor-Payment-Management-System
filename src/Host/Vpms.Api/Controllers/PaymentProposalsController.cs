using System.Security.Claims;
using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Vpms.Api.Auth;
using Vpms.Identity.Contracts;
using Vpms.Payments.Application;

namespace Vpms.Api.Controllers;

public sealed record PaymentCommentBody(string? Comment);

/// <summary>Payment proposals: grouped payables, prepared by one person, approved by a second. Release is a third step (see the release controller).</summary>
[ApiController, ApiVersion("1.0"), Route("api/v{version:apiVersion}/payments/proposals")]
public sealed class PaymentProposalsController(IPaymentProposalService proposals, IPermissionEvaluator permissions) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string? status, [FromQuery] Guid? legalEntityId, [FromQuery] int skip = 0, [FromQuery] int take = 50, CancellationToken ct = default)
    {
        var (allowed, scope) = await this.ScopeForAsync(permissions, Permissions.PaymentView, ct);
        return allowed ? Ok(await proposals.ListAsync(new ProposalFilter(status, legalEntityId), scope, skip, take, ct)) : Forbid();
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) =>
        await Has(Permissions.PaymentView, await proposals.EntityOfAsync(id, ct), ct) ? Ok(await proposals.GetAsync(id, ct)) : NotFound();

    [HttpGet("{id:guid}/recheck")]
    public async Task<IActionResult> Recheck(Guid id, CancellationToken ct) =>
        await Has(Permissions.PaymentView, await proposals.EntityOfAsync(id, ct), ct) ? Ok(await proposals.PreviewRecheckAsync(id, ct)) : NotFound();

    [HttpPost]
    public async Task<IActionResult> Create(CreateProposals body, CancellationToken ct) =>
        await Has(Permissions.PaymentPrepare, body.LegalEntityId, ct) ? Ok(await proposals.CreateAsync(body, ct)) : NotFound();

    [HttpDelete("{id:guid}/requests/{requestId:guid}")]
    public async Task<IActionResult> RemoveRequest(Guid id, Guid requestId, CancellationToken ct) =>
        await Has(Permissions.PaymentPrepare, await proposals.EntityOfAsync(id, ct), ct) ? Ok(await proposals.RemoveRequestAsync(id, requestId, ct)) : NotFound();

    [HttpPost("{id:guid}/submit")]
    public async Task<IActionResult> Submit(Guid id, CancellationToken ct) =>
        await Has(Permissions.PaymentPrepare, await proposals.EntityOfAsync(id, ct), ct) ? Ok(await proposals.SubmitAsync(id, ct)) : NotFound();

    [HttpPost("{id:guid}/approve")]
    public async Task<IActionResult> Approve(Guid id, PaymentCommentBody body, CancellationToken ct) =>
        await Has(Permissions.PaymentApprove, await proposals.EntityOfAsync(id, ct), ct) ? Ok(await proposals.ApproveAsync(id, body.Comment, ct)) : NotFound();

    [HttpPost("{id:guid}/send-back")]
    public async Task<IActionResult> SendBack(Guid id, PaymentReasonBody body, CancellationToken ct) =>
        await Has(Permissions.PaymentApprove, await proposals.EntityOfAsync(id, ct), ct) ? Ok(await proposals.SendBackAsync(id, body.Reason, ct)) : NotFound();

    [HttpPost("{id:guid}/cancel")]
    public async Task<IActionResult> Cancel(Guid id, PaymentReasonBody body, CancellationToken ct)
    {
        var entity = await proposals.EntityOfAsync(id, ct);
        return await Has(Permissions.PaymentPrepare, entity, ct) || await Has(Permissions.PaymentApprove, entity, ct) ? Ok(await proposals.CancelAsync(id, body.Reason, ct)) : NotFound();
    }

    private Guid UserId => Guid.Parse(User.FindFirstValue(VpmsClaims.UserId)!);

    private Task<bool> Has(string permission, Guid legalEntityId, CancellationToken ct) => permissions.HasAsync(UserId, permission, legalEntityId, ct);
}
