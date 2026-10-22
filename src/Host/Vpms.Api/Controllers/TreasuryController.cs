using System.Security.Claims;
using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Vpms.Api.Auth;
using Vpms.Identity.Contracts;
using Vpms.Payments.Application;

namespace Vpms.Api.Controllers;

/// <summary>Release of approved proposals and the prepared payment instruction. This system prepares and hands over; it never sends money.</summary>
[ApiController, ApiVersion("1.0"), Route("api/v{version:apiVersion}/payments")]
public sealed class TreasuryController(ITreasuryService treasury, IPaymentProposalService proposals, IPermissionEvaluator permissions) : ControllerBase
{
    [HttpPost("proposals/{id:guid}/release")]
    [Vpms.Api.Auth.RequireStepUp]
    public async Task<IActionResult> Release(Guid id, CancellationToken ct) =>
        await Has(Permissions.PaymentRelease, await proposals.EntityOfAsync(id, ct), ct) ? Ok(await treasury.ReleaseAsync(id, ct)) : NotFound();

    [HttpGet("proposals/{id:guid}/authorizations")]
    public async Task<IActionResult> Authorizations(Guid id, CancellationToken ct) =>
        await Has(Permissions.PaymentView, await proposals.EntityOfAsync(id, ct), ct) ? Ok(await treasury.AuthorizationsAsync(id, ct)) : NotFound();

    [HttpGet("proposals/{id:guid}/instruction")]
    public async Task<IActionResult> Instruction(Guid id, CancellationToken ct)
    {
        if (!await Has(Permissions.PaymentView, await proposals.EntityOfAsync(id, ct), ct)) return NotFound();
        var i = await treasury.GetInstructionAsync(id, ct);
        return i is null ? NotFound() : Ok(i);
    }

    [HttpGet("instructions/{id:guid}/verify")]
    public async Task<IActionResult> Verify(Guid id, CancellationToken ct) =>
        await Has(Permissions.PaymentInstructionDownload, await treasury.EntityOfInstructionAsync(id, ct), ct) ? Ok(await treasury.VerifyAsync(id, ct)) : NotFound();

    [HttpGet("instructions/{id:guid}/download")]
    [Vpms.Api.Auth.RequireStepUp]
    public async Task<IActionResult> Download(Guid id, [FromQuery] string kind = "csv", CancellationToken ct = default)
    {
        if (!await Has(Permissions.PaymentInstructionDownload, await treasury.EntityOfInstructionAsync(id, ct), ct)) return NotFound();
        var file = await treasury.DownloadAsync(id, kind, ct);
        Response.Headers.CacheControl = "no-store";
        return File(file.Content, file.ContentType, file.FileName);
    }

    private Guid UserId => Guid.Parse(User.FindFirstValue(VpmsClaims.UserId)!);

    private Task<bool> Has(string permission, Guid legalEntityId, CancellationToken ct) => permissions.HasAsync(UserId, permission, legalEntityId, ct);
}
