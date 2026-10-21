using Vpms.Identity.Application;

namespace Vpms.Api.Auth;

/// <summary>
/// Makes sure the OIDC scheme for a provider exists before the authentication middleware handles its callback,
/// so a callback served by an instance that never saw the login still works.
/// </summary>
public sealed class OidcCallbackSchemeMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, DynamicOidcSchemes dynamicSchemes, IIdentityStore store,
        ISecretResolver secrets)
    {
        if (DynamicOidcSchemes.TryParseCallback(context.Request.Path, out var providerId))
        {
            var provider = await store.FindProviderAsync(providerId, context.RequestAborted);
            if (provider is { Enabled: true })
            {
                await dynamicSchemes.EnsureAsync(provider, secrets, context.RequestAborted);
            }
        }

        await next(context);
    }
}
