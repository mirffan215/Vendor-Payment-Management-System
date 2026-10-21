using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Options;
using Vpms.Identity.Application;
using Vpms.Identity.Domain;

namespace Vpms.Api.Auth;

/// <summary>
/// Registers one OpenID Connect scheme per tenant identity provider at runtime. The scheme and its callback path
/// are derived from the provider id; provider settings come from the database, not from static configuration.
/// </summary>
public sealed class DynamicOidcSchemes(
    IAuthenticationSchemeProvider schemes,
    IOptionsMonitorCache<OpenIdConnectOptions> optionsCache,
    IEnumerable<IPostConfigureOptions<OpenIdConnectOptions>> postConfigures,
    Microsoft.Extensions.Options.IOptions<AuthOptions> authOptions,
    OidcBackchannelOptions backchannel)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public static string SchemeName(Guid providerId) => $"oidc-{providerId:N}";

    public static string CallbackPath(Guid providerId) => $"/auth/callback/{providerId:N}";

    public static bool TryParseCallback(PathString path, out Guid providerId)
    {
        providerId = Guid.Empty;
        const string prefix = "/auth/callback/";
        return path.StartsWithSegments(prefix.TrimEnd('/'), out var rest)
               && Guid.TryParseExact(rest.Value?.Trim('/'), "N", out providerId);
    }

    public async Task<string> EnsureAsync(IdentityProviderConfig provider, ISecretResolver secrets, CancellationToken ct)
    {
        var name = SchemeName(provider.Id);
        if (await schemes.GetSchemeAsync(name) is not null)
        {
            return name;
        }

        if (!IdentityProviderConfig.IsAuthorityAcceptable(provider.Kind, provider.Authority))
        {
            throw new InvalidOperationException("Identity provider authority is not acceptable.");
        }

        var secret = await secrets.ResolveAsync(provider.ClientSecretRef, ct)
                     ?? throw new InvalidOperationException("Identity provider client secret is not available.");

        await _gate.WaitAsync(ct);
        try
        {
            if (await schemes.GetSchemeAsync(name) is not null)
            {
                return name;
            }

            var options = new OpenIdConnectOptions
            {
                Authority = provider.Authority,
                ClientId = provider.ClientId,
                ClientSecret = secret,
                ResponseType = "code",
                UsePkce = true,
                CallbackPath = CallbackPath(provider.Id),
                SignedOutCallbackPath = $"/auth/signout-callback/{provider.Id:N}",
                SignInScheme = VpmsAuth.CookieScheme,
                SaveTokens = false,
                GetClaimsFromUserInfoEndpoint = false,
                MapInboundClaims = false,
                RequireHttpsMetadata = !authOptions.Value.AllowInsecureMetadata,
                BackchannelHttpHandler = backchannel.Handler,
                Events = OidcEvents.Create(provider.Id),
            };
            options.Scope.Clear();
            foreach (var scope in provider.Scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                options.Scope.Add(scope);
            }

            options.TokenValidationParameters.NameClaimType = "name";
            options.TokenValidationParameters.ValidateIssuer = true;
            options.TokenValidationParameters.ValidateAudience = true;
            options.TokenValidationParameters.ValidateLifetime = true;

            foreach (var post in postConfigures)
            {
                post.PostConfigure(name, options);
            }

            optionsCache.TryAdd(name, options);
            schemes.AddScheme(new AuthenticationScheme(name, provider.DisplayName, typeof(OpenIdConnectHandler)));
        }
        finally
        {
            _gate.Release();
        }

        return name;
    }
}
