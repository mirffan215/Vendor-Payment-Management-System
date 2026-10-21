using System.Globalization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Vpms.BuildingBlocks.Tenancy;
using Vpms.Identity.Application;

namespace Vpms.Api.Auth;

/// <summary>Rejects sessions past their absolute lifetime or whose user was disabled (re-checked every minute).</summary>
public static class CookieSessionValidator
{
    public static async Task ValidateAsync(CookieValidatePrincipalContext ctx)
    {
        var sp = ctx.HttpContext.RequestServices;
        var options = sp.GetRequiredService<IOptions<AuthOptions>>().Value;
        var clock = sp.GetRequiredService<TimeProvider>();
        var principal = ctx.Principal;

        if (principal is null
            || !Guid.TryParse(principal.FindFirst(VpmsClaims.UserId)?.Value, out var userId)
            || !Guid.TryParse(principal.FindFirst(VpmsClaims.TenantId)?.Value, out var tenantId)
            || !long.TryParse(principal.FindFirst(VpmsClaims.AuthTime)?.Value, CultureInfo.InvariantCulture, out var authTime)
            || principal.FindFirst(VpmsClaims.Context)?.Value != VpmsClaims.InternalContext)
        {
            await Reject(ctx);
            return;
        }

        if (clock.GetUtcNow() - DateTimeOffset.FromUnixTimeSeconds(authTime) > options.AbsoluteTimeout)
        {
            await Reject(ctx);
            return;
        }

        var cache = sp.GetRequiredService<IMemoryCache>();
        var active = await cache.GetOrCreateAsync($"session-active:{userId:N}", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(1);
            sp.GetRequiredService<TenantContext>().SetTenant(tenantId);
            return await sp.GetRequiredService<IIdentityStore>().IsUserActiveAsync(userId, ctx.HttpContext.RequestAborted);
        });

        if (active != true)
        {
            await Reject(ctx);
        }
    }

    private static async Task Reject(CookieValidatePrincipalContext ctx)
    {
        ctx.RejectPrincipal();
        await ctx.HttpContext.SignOutAsync(VpmsAuth.CookieScheme);
    }
}
