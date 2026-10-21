using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Vpms.Identity.Application;

namespace Vpms.Api.Auth;

internal static class OidcEvents
{
    public static OpenIdConnectEvents Create(Guid providerId) => new()
    {
        OnTokenValidated = ctx => OnTokenValidated(ctx, providerId),
        OnRemoteFailure = ctx =>
        {
            ctx.Response.Redirect("/auth/denied?reason=" + Uri.EscapeDataString(ctx.Failure?.Message is { } m && m.StartsWith("denied:", StringComparison.Ordinal) ? m[7..] : "failed"));
            ctx.HandleResponse();
            return Task.CompletedTask;
        },
    };

    private static async Task OnTokenValidated(TokenValidatedContext ctx, Guid providerId)
    {
        var sp = ctx.HttpContext.RequestServices;
        var store = sp.GetRequiredService<IIdentityStore>();
        var provisioner = sp.GetRequiredService<UserProvisioner>();
        var clock = sp.GetRequiredService<TimeProvider>();

        var provider = await store.FindProviderAsync(providerId, ctx.HttpContext.RequestAborted);
        if (provider is null || ctx.Principal is null)
        {
            ctx.Fail("denied:ProviderDisabled");
            return;
        }

        var external = ctx.Principal;
        var emailVerified = external.FindFirstValue("email_verified");
        var email = string.Equals(emailVerified, "false", StringComparison.OrdinalIgnoreCase)
            ? null
            : external.FindFirstValue("email") ?? external.FindFirstValue("preferred_username");

        // Entra ID: sub is pairwise per application; oid is the stable directory object id.
        var subject = external.FindFirstValue("oid") ?? external.FindFirstValue("sub");

        var result = await provisioner.SignInAsync(provider,
            new ExternalIdentity(subject ?? "", email, external.FindFirstValue("name")), ctx.HttpContext.RequestAborted);

        if (!result.Succeeded)
        {
            ctx.Fail("denied:" + result.Denial);
            return;
        }

        var user = result.User!;
        // Only server-derived claims enter the session; nothing from the external token is trusted or copied.
        var identity = new ClaimsIdentity(
        [
            new Claim(VpmsClaims.UserId, user.UserId.ToString()),
            new Claim(VpmsClaims.TenantId, user.TenantId.ToString()),
            new Claim(VpmsClaims.ProviderId, user.ProviderId.ToString()),
            new Claim(VpmsClaims.Context, VpmsClaims.InternalContext),
            new Claim(VpmsClaims.AuthTime, clock.GetUtcNow().ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new Claim("name", user.DisplayName),
            new Claim("email", user.Email),
            new Claim("locale", user.Locale),
        ], "vpms", "name", "role");
        ctx.Principal = new ClaimsPrincipal(identity);
    }
}
