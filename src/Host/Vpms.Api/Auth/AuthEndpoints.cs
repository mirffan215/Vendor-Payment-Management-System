using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Vpms.CustomerAdmin.Contracts;
using Vpms.Identity.Application;

namespace Vpms.Api.Auth;

public static class AuthEndpoints
{
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

        auth.MapGet("/login", async (Guid provider, string? returnUrl, HttpContext http, IIdentityStore store,
            ISecretResolver secrets, DynamicOidcSchemes schemes, CancellationToken ct) =>
        {
            var config = await store.FindProviderAsync(provider, ct);
            if (config is not { Enabled: true })
            {
                return Results.NotFound();
            }

            var scheme = await schemes.EnsureAsync(config, secrets, ct);
            return Results.Challenge(new AuthenticationProperties { RedirectUri = SafeReturnUrl(returnUrl) }, [scheme]);
        });

        auth.MapGet("/denied", (string? reason) =>
            Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Sign-in was not permitted.",
                type: "https://vpms.dev/problems/signin-denied", extensions: new Dictionary<string, object?> { ["reason"] = reason }));

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
