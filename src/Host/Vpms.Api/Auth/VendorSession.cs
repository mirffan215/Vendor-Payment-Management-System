using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Vpms.BuildingBlocks.Audit;
using Vpms.BuildingBlocks.Tenancy;
using Vpms.VendorPortal.Application;

namespace Vpms.Api.Auth;

public static class VendorClaims
{
    public const string VendorId = "vpms:vid";
    public const string VendorContext = "vendor";
    public const string PortalPolicy = "VendorPortal";
}

public static class VendorSessionFactory
{
    /// <summary>Builds the session principal from server-side facts only.</summary>
    public static ClaimsPrincipal Create(PortalSession s, DateTimeOffset now) => new(new ClaimsIdentity(
    [
        new Claim(VpmsClaims.UserId, s.UserId.ToString()),
        new Claim(VpmsClaims.TenantId, s.TenantId.ToString()),
        new Claim(VendorClaims.VendorId, s.VendorId.ToString()),
        new Claim(VpmsClaims.Context, VendorClaims.VendorContext),
        new Claim(VpmsClaims.AuthTime, now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)),
        new Claim("name", s.DisplayName),
        new Claim("email", s.Email),
    ], "vpms-vendor", "name", "role"));
}

/// <summary>Rejects vendor sessions past their absolute lifetime or whose user was disabled (re-checked every minute).</summary>
public static class VendorCookieValidator
{
    public static async Task ValidateAsync(CookieValidatePrincipalContext ctx)
    {
        var sp = ctx.HttpContext.RequestServices;
        var clock = sp.GetRequiredService<TimeProvider>();
        var p = ctx.Principal;
        var absolute = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<Vpms.VendorPortal.Application.PortalOptions>>().Value.SessionAbsoluteTimeout;

        if (p is null
            || p.FindFirst(VpmsClaims.Context)?.Value != VendorClaims.VendorContext
            || !Guid.TryParse(p.FindFirst(VpmsClaims.UserId)?.Value, out var userId)
            || !Guid.TryParse(p.FindFirst(VpmsClaims.TenantId)?.Value, out var tenantId)
            || !Guid.TryParse(p.FindFirst(VendorClaims.VendorId)?.Value, out _)
            || !long.TryParse(p.FindFirst(VpmsClaims.AuthTime)?.Value, CultureInfo.InvariantCulture, out var authTime)
            || clock.GetUtcNow() - DateTimeOffset.FromUnixTimeSeconds(authTime) > absolute)
        {
            await Reject(ctx);
            return;
        }

        var active = await sp.GetRequiredService<IMemoryCache>().GetOrCreateAsync($"vendor-session-active:{userId:N}", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(1);
            sp.GetRequiredService<TenantContext>().SetTenant(tenantId);
            return await sp.GetRequiredService<IPortalAuthService>().IsSessionValidAsync(userId, ctx.HttpContext.RequestAborted);
        });

        if (active != true) await Reject(ctx);
    }

    private static async Task Reject(CookieValidatePrincipalContext ctx)
    {
        ctx.RejectPrincipal();
        await ctx.HttpContext.SignOutAsync(VpmsAuth.VendorCookieScheme);
    }
}

/// <summary>
/// Authenticates the vendor cookie for portal API calls and puts the vendor into the request scope. The vendor id comes only
/// from the server-issued session, never from the URL, headers or body.
/// </summary>
public sealed class VendorSessionMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext http, VendorContext vendor, AuditContext audit)
    {
        if (http.Request.Path.StartsWithSegments("/api/v1/portal"))
        {
            var result = await http.AuthenticateAsync(VpmsAuth.VendorCookieScheme);
            if (result.Succeeded && Guid.TryParse(result.Principal.FindFirst(VendorClaims.VendorId)?.Value, out var vendorId))
            {
                http.User = result.Principal;
                vendor.SetVendor(vendorId);
                audit.ActorType = "Vendor";
            }
        }

        await next(http);
    }
}
