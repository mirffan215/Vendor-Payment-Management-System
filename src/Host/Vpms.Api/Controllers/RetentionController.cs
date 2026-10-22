using System.Security.Claims;
using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Vpms.Api.Auth;
using Vpms.CustomerAdmin.Application;
using Vpms.CustomerAdmin.Domain;
using Vpms.Identity.Contracts;

namespace Vpms.Api.Controllers;

public sealed record RetentionProposeRequest(RetentionSettings Settings, string Note);

public sealed record RetentionDecisionRequest(string? Comment);

public sealed record PlaceHoldRequest(string Scope, Guid? LegalEntityId, Guid? VendorId, string Reason);

public sealed record ReleaseHoldRequest(string Comment);

/// <summary>How long data is kept, what is on legal hold, and what has passed its retention period (ADR-0024).</summary>
[ApiController, ApiVersion("1.0"), Route("api/v{version:apiVersion}/admin")]
public sealed class RetentionController(IRetentionPolicyService policy, ILegalHoldService holds, IRetentionReportService report, IPermissionEvaluator permissions) : ControllerBase
{
    [HttpGet("retention")]
    public async Task<IActionResult> State(CancellationToken ct) => await CanSee(ct) ? Ok(await policy.GetAsync(ct)) : Forbid();

    [HttpPut("retention")]
    public async Task<IActionResult> Propose(RetentionProposeRequest body, CancellationToken ct) =>
        await Has(Permissions.RetentionConfigure, ct) ? Ok(await policy.ProposeAsync(body.Settings, body.Note, ct)) : Forbid();

    [HttpPost("retention/{id:guid}/approve")]
    [RequireStepUp]
    public async Task<IActionResult> Approve(Guid id, RetentionDecisionRequest body, CancellationToken ct) =>
        await Has(Permissions.RetentionApprovePolicy, ct) ? Ok(await policy.ApproveAsync(id, body.Comment, ct)) : Forbid();

    [HttpPost("retention/{id:guid}/reject")]
    public async Task<IActionResult> Reject(Guid id, RetentionDecisionRequest body, CancellationToken ct) =>
        await Has(Permissions.RetentionApprovePolicy, ct) ? Ok(await policy.RejectAsync(id, body.Comment ?? "", ct)) : Forbid();

    [HttpPost("retention/{id:guid}/cancel")]
    public async Task<IActionResult> Cancel(Guid id, CancellationToken ct) =>
        await Has(Permissions.RetentionConfigure, ct) ? Ok(await policy.CancelAsync(id, ct)) : Forbid();

    [HttpGet("retention/report")]
    public async Task<IActionResult> Report(CancellationToken ct) => await CanSee(ct) ? Ok(await report.BuildAsync(ct)) : Forbid();

    [HttpGet("legal-holds")]
    public async Task<IActionResult> Holds([FromQuery] bool activeOnly = true, CancellationToken ct = default) =>
        await CanSee(ct) ? Ok(await holds.ListAsync(activeOnly, ct)) : Forbid();

    [HttpPost("legal-holds")]
    public async Task<IActionResult> Place(PlaceHoldRequest body, CancellationToken ct) =>
        await Has(Permissions.RetentionHold, ct) ? Ok(await holds.PlaceAsync(body.Scope, body.LegalEntityId, body.VendorId, body.Reason, ct)) : Forbid();

    [HttpPost("legal-holds/{id:guid}/release")]
    [RequireStepUp]
    public async Task<IActionResult> Release(Guid id, ReleaseHoldRequest body, CancellationToken ct) =>
        await Has(Permissions.RetentionHold, ct) ? Ok(await holds.ReleaseAsync(id, body.Comment, ct)) : Forbid();

    private Guid UserId => Guid.Parse(User.FindFirstValue(VpmsClaims.UserId)!);

    private Task<bool> Has(string permission, CancellationToken ct) => permissions.HasAsync(UserId, permission, null, ct);

    private async Task<bool> CanSee(CancellationToken ct) =>
        await Has(Permissions.RetentionConfigure, ct) || await Has(Permissions.RetentionApprovePolicy, ct) || await Has(Permissions.RetentionHold, ct);
}
