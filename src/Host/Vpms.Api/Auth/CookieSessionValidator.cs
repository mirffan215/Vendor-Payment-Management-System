using System.Globalization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Vpms.BuildingBlocks.Tenancy;
using Vpms.Identity.Application;

namespace Vpms.Api.Auth;

/// <summary>Rejects sessions past their absolute lifetime, whose user was disabled, or that began before the person ended all sessions (re-checked every minute).</summary>
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
        var state = await cache.GetOrCreateAsync(StateKey(userId), async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(1);
            sp.GetRequiredService<TenantContext>().SetTenant(tenantId);
            return await sp.GetRequiredService<IIdentityStore>().GetSessionStateAsync(userId, ctx.HttpContext.RequestAborted);
        });

        // A suspended or offboarded organisation has no sessions.
        var tenantActive = await cache.GetOrCreateAsync($"tenant-active:{tenantId:N}", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(1);
            return await sp.GetRequiredService<Vpms.CustomerAdmin.Contracts.ITenantLookup>().IsActiveAsync(tenantId, ctx.HttpContext.RequestAborted);
        });
        if (tenantActive != true)
        {
            await Reject(ctx);
            return;
        }

        // Disabled, or the person (or an administrator) ended all sessions at or after this one began.
        if (state is not { Active: true } || (state.ValidFrom is { } from && authTime <= from.ToUnixTimeSeconds()))
        {
            await Reject(ctx);
        }
    }

    /// <summary>Forget what is known about a person so the next request re-reads it (used right after sessions are revoked on this instance).</summary>
    public static string StateKey(Guid userId) => $"session-state:{userId:N}";

    private static async Task Reject(CookieValidatePrincipalContext ctx)
    {
        ctx.RejectPrincipal();
        await ctx.HttpContext.SignOutAsync(VpmsAuth.CookieScheme);
    }
}
