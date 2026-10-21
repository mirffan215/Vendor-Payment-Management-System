using System.Security.Claims;
using Asp.Versioning;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Vpms.Api.Auth;
using Vpms.Identity.Contracts;
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
