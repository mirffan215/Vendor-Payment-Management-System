using System.Security.Claims;
using Asp.Versioning;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Vpms.Api.Auth;
using Vpms.Identity.Contracts;
using Vpms.Invoices.Application;
using Vpms.Vendors.Application;
using Vpms.Vendors.Contracts;
using Vpms.VendorPortal.Application;

namespace Vpms.Api.Controllers;

public sealed record InviteQuery(string? Tenant, string? Token);

public sealed record AcceptInviteRequest(string Tenant, string Token, string Password);

public sealed record EnrollRequest(string Tenant, string Token, string Code);

public sealed record PortalLoginRequest(string Tenant, string Email, string Password);

public sealed record MfaRequest(string Challenge, string Code);

public sealed record InviteUserRequest(string Email, string DisplayName);

/// <summary>Sign-in flow for vendor users. Everything here is anonymous, rate limited, and answers failures uniformly.</summary>
[ApiController, ApiVersionNeutral, Route("portal/auth"), EnableRateLimiting(Infrastructure.RateLimiting.PortalAuthPolicy)]
public sealed class PortalAuthController(IPortalAuthService auth, TimeProvider clock) : ControllerBase
{
    [AllowAnonymous]
    [HttpGet("invite")]
    public async Task<InviteInfo> Invite([FromQuery] InviteQuery q, CancellationToken ct) => await auth.GetInviteAsync(q.Tenant ?? "", q.Token ?? "", ct);

    [AllowAnonymous]
    [HttpPost("invite/accept")]
    public async Task<AcceptResult> Accept(AcceptInviteRequest body, CancellationToken ct) => await auth.AcceptInviteAsync(body.Tenant, body.Token, body.Password, ct);

    [AllowAnonymous]
    [HttpPost("invite/enroll")]
    public async Task<IActionResult> Enroll(EnrollRequest body, CancellationToken ct)
    {
        var result = await auth.CompleteEnrollmentAsync(body.Tenant, body.Token, body.Code, ct);
        await SignInAsync(result.Session);
        return Ok(new { recoveryCodes = result.RecoveryCodes });
    }

    [AllowAnonymous]
    [HttpPost("login")]
    public async Task<LoginChallenge> Login(PortalLoginRequest body, CancellationToken ct) => await auth.LoginAsync(body.Tenant, body.Email, body.Password, ct);

    [AllowAnonymous]
    [HttpPost("mfa")]
    public async Task<IActionResult> Mfa(MfaRequest body, CancellationToken ct)
    {
        var session = await auth.VerifyMfaAsync(body.Challenge, body.Code, ct);
        await SignInAsync(session);
        return NoContent();
    }

    [HttpPost("logout"), Authorize(AuthenticationSchemes = VpmsAuth.VendorCookieScheme, Policy = VendorClaims.PortalPolicy)]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(VpmsAuth.VendorCookieScheme);
        return NoContent();
    }

    private Task SignInAsync(PortalSession session) =>
        HttpContext.SignInAsync(VpmsAuth.VendorCookieScheme, VendorSessionFactory.Create(session, clock.GetUtcNow()),
            new AuthenticationProperties { IsPersistent = false });
}

[ApiController, ApiVersion("1.0"), Route("api/v{version:apiVersion}/portal"), Authorize(AuthenticationSchemes = VpmsAuth.VendorCookieScheme, Policy = VendorClaims.PortalPolicy)]
public sealed class PortalSessionController(IAntiforgery antiforgery, IVendorLookup vendors) : ControllerBase
{
    [HttpGet("session")]
    public async Task<IActionResult> Session(CancellationToken ct)
    {
        var vendorId = Guid.Parse(User.FindFirstValue(VendorClaims.VendorId)!);
        var tokens = antiforgery.GetAndStoreTokens(HttpContext);
        return Ok(new
        {
            userId = User.FindFirstValue(VpmsClaims.UserId),
            vendorId,
            vendorName = (await vendors.FindAsync(vendorId, ct))?.LegalName,
            email = User.FindFirstValue("email"),
            displayName = User.FindFirstValue("name"),
            csrfToken = tokens.RequestToken,
        });
    }
}

public sealed record ReplyRequest(string Body);

/// <summary>
/// What a signed-in vendor user can do. There is no vendor id in any route or body: the vendor comes from the session, and
/// the data layer filters every vendor-owned table to it, so another vendor's ids simply do not exist here (404).
/// </summary>
[ApiController, ApiVersion("1.0"), Route("api/v{version:apiVersion}/portal"), Authorize(AuthenticationSchemes = VpmsAuth.VendorCookieScheme, Policy = VendorClaims.PortalPolicy)]
public sealed class PortalVendorController(IVendorPortalService portal) : ControllerBase
{
    private const long MaxUpload = 25 * 1024 * 1024;

    [HttpGet("profile")]
    public async Task<PortalProfile> Profile(CancellationToken ct) => await portal.GetProfileAsync(ct);

    [HttpPut("profile")]
    public async Task<PortalProfile> UpdateProfile(UpdatePortalProfile body, CancellationToken ct) => await portal.UpdateProfileAsync(body, ct);

    [HttpGet("entities")]
    public async Task<IReadOnlyList<PortalEntityLink>> Entities(CancellationToken ct) => await portal.ListEntityLinksAsync(ct);

    [HttpGet("documents")]
    public async Task<IReadOnlyList<VendorDocumentDto>> Documents(CancellationToken ct) => await portal.ListDocumentsAsync(ct);

    [HttpPost("documents"), RequestSizeLimit(MaxUpload)]
    public async Task<VendorDocumentDto> Upload([FromForm] string type, [FromForm] DateOnly? expiresOn, IFormFile file, CancellationToken ct)
    {
        await using var stream = file.OpenReadStream();
        return await portal.UploadDocumentAsync(type, expiresOn, file.FileName, stream, ct);
    }

    [HttpGet("documents/{documentId:guid}/file")]
    public async Task<IActionResult> Download(Guid documentId, CancellationToken ct)
    {
        var file = await portal.OpenDocumentAsync(documentId, ct);
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        return File(file.Content, file.ContentType, file.FileName);
    }

    [HttpGet("bank-details")]
    public async Task<IReadOnlyList<BankDetailsDto>> BankDetails(CancellationToken ct) => await portal.ListBankDetailsAsync(ct);

    [HttpGet("bank-detail-changes")]
    public async Task<IReadOnlyList<PortalBankChange>> BankChanges(CancellationToken ct) => await portal.ListBankChangesAsync(ct);

    [HttpPost("bank-detail-changes")]
    public async Task<PortalBankChange> ProposeBank(ProposeBankDetails body, CancellationToken ct) => await portal.ProposeBankDetailsAsync(body, ct);

    [HttpPost("bank-detail-changes/{changeId:guid}/cancel")]
    public async Task<PortalBankChange> CancelBank(Guid changeId, CancellationToken ct) => await portal.CancelBankChangeAsync(changeId, ct);

    [HttpGet("requests")]
    public async Task<IReadOnlyList<RequestDto>> Requests(CancellationToken ct) => await portal.ListRequestsAsync(ct);

    [HttpPost("requests/{requestId:guid}/reply")]
    public async Task<RequestDto> Reply(Guid requestId, ReplyRequest body, CancellationToken ct) => await portal.ReplyToRequestAsync(requestId, body.Body, ct);
}

/// <summary>Invoices from the vendor's side: submit with a file, and follow the status. Verification detail stays internal.</summary>
[ApiController, ApiVersion("1.0"), Route("api/v{version:apiVersion}/portal/invoices"), Authorize(AuthenticationSchemes = VpmsAuth.VendorCookieScheme, Policy = VendorClaims.PortalPolicy)]
public sealed class PortalInvoicesController(IPortalInvoiceService invoices) : ControllerBase
{
    private const long MaxUpload = 25 * 1024 * 1024;

    [HttpGet]
    public async Task<IReadOnlyList<PortalInvoiceDto>> List(CancellationToken ct) => await invoices.ListAsync(ct);

    [HttpGet("{id:guid}")]
    public async Task<PortalInvoiceDto> Get(Guid id, CancellationToken ct) => await invoices.GetAsync(id, ct);

    [HttpPost, RequestSizeLimit(MaxUpload)]
    public async Task<PortalInvoiceDto> Submit(
        [FromForm] Guid legalEntityId, [FromForm] string invoiceNumber, [FromForm] DateOnly invoiceDate, [FromForm] DateOnly? dueDate,
        [FromForm] string currencyCode, [FromForm] decimal subtotal, [FromForm] decimal taxTotal, [FromForm] decimal total, [FromForm] string? poReference,
        IFormFile file, CancellationToken ct)
    {
        await using var stream = file.OpenReadStream();
        var data = new InvoiceData(invoiceNumber, invoiceDate, dueDate, currencyCode, subtotal, taxTotal, total, poReference, null, []);
        return await invoices.SubmitAsync(new PortalSubmitInvoice(legalEntityId, data), file.FileName, stream, ct);
    }
}

/// <summary>Customer staff manage who at a vendor can use the portal.</summary>
[ApiController, ApiVersion("1.0"), Route("api/v{version:apiVersion}"), Authorize(Policy = Permissions.VendorPortalManage)]
public sealed class PortalAdminController(IPortalAdminService service) : ControllerBase
{
    [HttpGet("vendors/{vendorId:guid}/portal-users")]
    public async Task<IReadOnlyList<PortalUserDto>> List(Guid vendorId, CancellationToken ct) => await service.ListUsersAsync(vendorId, ct);

    [HttpPost("vendors/{vendorId:guid}/portal-users")]
    public async Task<InviteResult> Invite(Guid vendorId, InviteUserRequest body, CancellationToken ct) => await service.InviteUserAsync(vendorId, body.Email, body.DisplayName, ct);

    [HttpPost("portal-users/{userId:guid}/reissue-invite")]
    public async Task<InviteResult> Reissue(Guid userId, CancellationToken ct) => await service.ReissueInviteAsync(userId, ct);

    [HttpPost("portal-users/{userId:guid}/enable")]
    public async Task<PortalUserDto> Enable(Guid userId, CancellationToken ct) => await service.SetStatusAsync(userId, true, ct);

    [HttpPost("portal-users/{userId:guid}/disable")]
    public async Task<PortalUserDto> Disable(Guid userId, CancellationToken ct) => await service.SetStatusAsync(userId, false, ct);

    [HttpPost("portal-users/{userId:guid}/request-mfa-reset")]
    public async Task<PortalUserDto> RequestReset(Guid userId, CancellationToken ct) => await service.RequestMfaResetAsync(userId, ct);

    [HttpPost("portal-users/{userId:guid}/approve-mfa-reset")]
    public async Task<InviteResult> ApproveReset(Guid userId, CancellationToken ct) => await service.ApproveMfaResetAsync(userId, ct);
}
