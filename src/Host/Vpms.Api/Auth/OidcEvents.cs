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

        if (!await sp.GetRequiredService<Vpms.CustomerAdmin.Contracts.ITenantLookup>().IsActiveAsync(provider.TenantId, ctx.HttpContext.RequestAborted))
        {
            ctx.Fail("denied:ProviderDisabled");
            return;
        }

        var external = ctx.Principal;
        if (ctx.Properties?.Items.ContainsKey(AuthEndpoints.StepUpItem) == true)
        {
            // We asked for a fresh authentication (max_age=0), so the token must say when it happened and it must be recent. A provider that ignores the request fails here.
            var asserted = long.TryParse(external.FindFirstValue("auth_time"), System.Globalization.CultureInfo.InvariantCulture, out var at) ? DateTimeOffset.FromUnixTimeSeconds(at) : (DateTimeOffset?)null;
            if (asserted is null || clock.GetUtcNow() - asserted.Value > TimeSpan.FromMinutes(2))
            {
                ctx.Fail("denied:StepUpNotPerformed");
                return;
            }
        }

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
