using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Vpms.CustomerAdmin.Contracts;
using Vpms.Identity.Application;

namespace Vpms.Api.Auth;

public static class AuthEndpoints
{
    /// <summary>Marks a sign-in that was started to re-authenticate an existing session; the identity provider must prove it happened just now.</summary>
    public const string StepUpItem = "vpms:step_up";

    public static void MapAuthEndpoints(this WebApplication app)
    {
        var auth = app.MapGroup("/auth").AllowAnonymous().RequireRateLimiting(Infrastructure.RateLimiting.AuthPolicy);

        // Public: lists the sign-in options of a tenant (id and display name only).
        auth.MapGet("/providers", async (string tenant, ITenantLookup tenants, IIdentityStore store, CancellationToken ct) =>
        {
            var tenantId = await tenants.FindTenantIdBySlugAsync(tenant, ct);
            return tenantId is null
                ? Results.Ok(Array.Empty<ProviderSummary>())
                : Results.Ok(await store.ListEnabledProvidersAsync(tenantId.Value, ct));
        });

        auth.MapGet("/login", async (Guid provider, string? returnUrl, HttpContext http, IIdentityStore store, ITenantLookup tenants,
            ISecretResolver secrets, DynamicOidcSchemes schemes, CancellationToken ct) =>
        {
            var config = await store.FindProviderAsync(provider, ct);
            if (config is not { Enabled: true } || !await tenants.IsActiveAsync(config.TenantId, ct))
            {
                return Results.NotFound();
            }

            var scheme = await schemes.EnsureAsync(config, secrets, ct);
            return Results.Challenge(new AuthenticationProperties { RedirectUri = SafeReturnUrl(returnUrl) }, [scheme]);
        });

        // Only the known denial codes are echoed back; anything else a caller puts in the address is replaced.
        auth.MapGet("/denied", (string? reason) =>
            Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Sign-in was not permitted.",
                type: "https://vpms.dev/problems/signin-denied",
                extensions: new Dictionary<string, object?> { ["reason"] = reason is not null && Enum.GetNames<Vpms.Identity.Application.SignInDenial>().Contains(reason) ? reason : "failed" }));

        // Asks the signed-in person's identity provider to authenticate them again (prompt=login, max_age=0). On return the session is rebuilt with a fresh sign-in time.
        auth.MapGet("/step-up", async (string? returnUrl, HttpContext http, IIdentityStore store, ISecretResolver secrets, DynamicOidcSchemes schemes, CancellationToken ct) =>
        {
            if (!Guid.TryParse(http.User.FindFirstValue(VpmsClaims.ProviderId), out var providerId)) return Results.Unauthorized();
            var config = await store.FindProviderAsync(providerId, ct);
            if (config is not { Enabled: true }) return Results.NotFound();
            var scheme = await schemes.EnsureAsync(config, secrets, ct);
            var props = new Microsoft.AspNetCore.Authentication.OpenIdConnect.OpenIdConnectChallengeProperties
            {
                RedirectUri = SafeReturnUrl(returnUrl),
                Prompt = "login",
                MaxAge = TimeSpan.Zero,
            };
            props.Items[StepUpItem] = "1";
            return Results.Challenge(props, [scheme]);
        }).RequireAuthorization();

        // Logout is POST-only and CSRF protected (see CsrfProtectionMiddleware).
        auth.MapPost("/logout", async (HttpContext http) =>
        {
            await http.SignOutAsync(VpmsAuth.CookieScheme);
            return Results.NoContent();
        }).RequireAuthorization();
    }

    /// <summary>Only local, path-absolute URLs are accepted to prevent open redirects.</summary>
    public static string SafeReturnUrl(string? returnUrl) =>
        !string.IsNullOrEmpty(returnUrl) && returnUrl[0] == '/' && (returnUrl.Length == 1 || (returnUrl[1] != '/' && returnUrl[1] != '\\'))
            ? returnUrl
            : "/";
}
