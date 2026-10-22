using System.Security.Claims;
using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Vpms.Api.Auth;
using Vpms.Identity.Contracts;
using Vpms.Invoices.Application;
using Vpms.Workflow.Application;

namespace Vpms.Api.Controllers;

/// <summary>The approver's side of the workflow: inbox, task detail, and decisions. Every rule is enforced by the engine, not here.</summary>
[ApiController, ApiVersion("1.0"), Route("api/v{version:apiVersion}")]
public sealed class ApprovalsController(IApprovalService approvals, IInvoiceService invoices, IPermissionEvaluator permissions) : ControllerBase
{
    [HttpGet("approvals/inbox")]
    public async Task<IActionResult> Inbox(CancellationToken ct)
    {
        var (allowed, scope) = await this.ScopeForAsync(permissions, Permissions.InvoiceApprove, ct);
        return allowed ? Ok(await approvals.InboxAsync(scope, ct)) : Forbid();
    }

    [HttpGet("approvals/tasks/{taskId:guid}")]
    public async Task<IActionResult> GetTask(Guid taskId, CancellationToken ct) =>
        await Has(Permissions.InvoiceApprove, await approvals.EntityOfTaskAsync(taskId, ct), ct) ? Ok(await approvals.GetRunOfTaskAsync(taskId, ct)) : NotFound();

    [HttpPost("approvals/tasks/{taskId:guid}/decision")]
    public async Task<IActionResult> Decide(Guid taskId, DecideRequest body, CancellationToken ct) =>
        await Has(Permissions.InvoiceApprove, await approvals.EntityOfTaskAsync(taskId, ct), ct) ? Ok(await approvals.DecideAsync(taskId, body, ct)) : NotFound();

    /// <summary>The approval history of an invoice: every run (an earlier run stays as history after a restart), its steps and decisions.</summary>
    [HttpGet("invoices/{invoiceId:guid}/approvals")]
    public async Task<IActionResult> ForInvoice(Guid invoiceId, CancellationToken ct) =>
        await Has(Permissions.InvoiceView, await invoices.EntityOfAsync(invoiceId, ct), ct) ? Ok(await approvals.RunsForSubjectAsync("Invoice", invoiceId, ct)) : NotFound();

    private Guid UserId => Guid.Parse(User.FindFirstValue(VpmsClaims.UserId)!);

    private Task<bool> Has(string permission, Guid legalEntityId, CancellationToken ct) => permissions.HasAsync(UserId, permission, legalEntityId, ct);
}
