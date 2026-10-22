using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Vpms.Identity.Contracts;
using Vpms.Vendors.Application;

namespace Vpms.Api.Controllers;

public sealed record ApproveChangeRequest(string? CallbackVerificationRef, string? Comment);

public sealed record CommentRequest(string Comment);

[ApiController, ApiVersion("1.0"), Route("api/v{version:apiVersion}")]
public sealed class BankDetailsController(IBankDetailsService service) : ControllerBase
{
    [HttpGet("vendors/{vendorId:guid}/bank-details"), Authorize(Policy = Permissions.VendorBankDetailsView)]
    public async Task<IReadOnlyList<BankDetailsDto>> List(Guid vendorId, CancellationToken ct) => await service.ListAsync(vendorId, ct);

    [HttpPost("vendors/{vendorId:guid}/bank-detail-changes"), Authorize(Policy = Permissions.VendorBankDetailsEdit)]
    public async Task<BankDetailChangeDto> Propose(Guid vendorId, ProposeBankDetails body, CancellationToken ct) => await service.ProposeAsync(vendorId, body, ct);

    [HttpGet("bank-detail-changes"), Authorize(Policy = Permissions.VendorBankDetailsView)]
    public async Task<IReadOnlyList<BankDetailChangeDto>> Changes([FromQuery] Guid? vendorId, [FromQuery] string? status, CancellationToken ct) =>
        await service.ListChangesAsync(vendorId, status, ct);

    [HttpPost("bank-detail-changes/{changeId:guid}/approve"), Authorize(Policy = Permissions.VendorBankDetailsApprove)]
    [Vpms.Api.Auth.RequireStepUp]
    public async Task<BankDetailChangeDto> Approve(Guid changeId, ApproveChangeRequest body, CancellationToken ct) =>
        await service.ApproveAsync(changeId, body.CallbackVerificationRef, body.Comment, ct);

    [HttpPost("bank-detail-changes/{changeId:guid}/reject"), Authorize(Policy = Permissions.VendorBankDetailsApprove)]
    public async Task<BankDetailChangeDto> Reject(Guid changeId, CommentRequest body, CancellationToken ct) => await service.RejectAsync(changeId, body.Comment, ct);

    [HttpPost("bank-detail-changes/{changeId:guid}/cancel"), Authorize(Policy = Permissions.VendorBankDetailsEdit)]
    public async Task<BankDetailChangeDto> Cancel(Guid changeId, CancellationToken ct) => await service.CancelAsync(changeId, ct);

    // POST rather than GET: the full account number is never cached or linked, and the call needs the antiforgery token.
    [HttpPost("bank-details/{bankDetailsId:guid}/reveal"), Authorize(Policy = Permissions.VendorBankDetailsReveal)]
    [Vpms.Api.Auth.RequireStepUp]
    public async Task<RevealedBankDetails> Reveal(Guid bankDetailsId, CancellationToken ct) => await service.RevealAsync(bankDetailsId, ct);
}
