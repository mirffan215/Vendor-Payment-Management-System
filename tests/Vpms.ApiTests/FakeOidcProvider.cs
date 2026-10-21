using System.Collections.Concurrent;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Vpms.ApiTests;

public sealed record FakeUser(string Subject, string Email, string Name, bool EmailVerified = true,
    string? IssuerOverride = null, string? AudienceOverride = null, IDictionary<string, object>? ExtraClaims = null);

/// <summary>An in-process OpenID Connect provider (discovery, JWKS, authorize with PKCE, token) for tests.</summary>
public sealed class FakeOidcProvider : IAsyncDisposable
{
    private readonly RSA _rsa;
    private readonly RsaSecurityKey _key;
    private readonly ConcurrentDictionary<string, Grant> _grants = new();
    private readonly WebApplication _app;

    public string Name { get; }
    public string Authority => $"https://idp.test/idp/{Name}";
    public string ClientId { get; }
    public string ClientSecret { get; }
    public FakeUser? NextUser { get; set; }
    public HttpMessageHandler Handler => _app.GetTestServer().CreateHandler();

    private sealed record Grant(FakeUser User, string ClientId, string Nonce, string CodeChallenge);

    private FakeOidcProvider(string name, string clientId, string clientSecret, WebApplication app, RSA rsa)
    {
        Name = name;
        ClientId = clientId;
        ClientSecret = clientSecret;
        _app = app;
        _rsa = rsa;
        _key = new RsaSecurityKey(rsa) { KeyId = "k1" };
    }

    public static async Task<FakeOidcProvider> StartAsync(string name, string clientId, string clientSecret)
    {
        var rsa = RSA.Create(2048);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        var app = builder.Build();
        var idp = new FakeOidcProvider(name, clientId, clientSecret, app, rsa);
        idp.Map(app);
        await app.StartAsync();
        return idp;
    }

    private void Map(WebApplication app)
    {
        var g = app.MapGroup($"/idp/{Name}");

        g.MapGet("/.well-known/openid-configuration", () => Results.Json(new Dictionary<string, object>
        {
            ["issuer"] = Authority,
            ["authorization_endpoint"] = $"{Authority}/authorize",
            ["token_endpoint"] = $"{Authority}/token",
            ["jwks_uri"] = $"{Authority}/jwks",
            ["response_types_supported"] = new[] { "code" },
            ["subject_types_supported"] = new[] { "public" },
            ["id_token_signing_alg_values_supported"] = new[] { "RS256" },
            ["code_challenge_methods_supported"] = new[] { "S256" },
        }));

        g.MapGet("/jwks", () =>
        {
            var jwk = JsonWebKeyConverter.ConvertFromRSASecurityKey(_key);
            return Results.Json(new { keys = new[] { new { kty = jwk.Kty, use = "sig", kid = jwk.Kid, alg = "RS256", n = jwk.N, e = jwk.E } } });
        });

        g.MapGet("/authorize", (HttpContext ctx) =>
        {
            var q = ctx.Request.Query;
            var user = NextUser ?? throw new InvalidOperationException("No fake user queued.");
            var code = Guid.NewGuid().ToString("N");
            _grants[code] = new Grant(user, q["client_id"]!, q["nonce"]!, q["code_challenge"]!);
            var redirect = q["redirect_uri"] + (q["redirect_uri"].ToString().Contains('?', StringComparison.Ordinal) ? "&" : "?")
                           + $"code={code}&state={Uri.EscapeDataString(q["state"]!)}";
            return Results.Redirect(redirect);
        });

        g.MapPost("/token", async (HttpContext ctx) =>
        {
            var form = await ctx.Request.ReadFormAsync();
            if (!_grants.TryRemove(form["code"].ToString(), out var grant)
                || form["client_id"] != ClientId || form["client_secret"] != ClientSecret
                || !PkceMatches(form["code_verifier"].ToString(), grant.CodeChallenge))
            {
                return Results.Json(new { error = "invalid_grant" }, statusCode: 400);
            }

            var u = grant.User;
            var claims = new Dictionary<string, object>
            {
                ["sub"] = u.Subject,
                ["email"] = u.Email,
                ["email_verified"] = u.EmailVerified,
                ["name"] = u.Name,
                ["nonce"] = grant.Nonce,
            };
            foreach (var (k, v) in u.ExtraClaims ?? new Dictionary<string, object>())
            {
                claims[k] = v;
            }

            var descriptor = new SecurityTokenDescriptor
            {
                Issuer = u.IssuerOverride ?? Authority,
                Audience = u.AudienceOverride ?? grant.ClientId,
                Claims = claims,
                IssuedAt = DateTime.UtcNow,
                NotBefore = DateTime.UtcNow.AddMinutes(-1),
                Expires = DateTime.UtcNow.AddMinutes(5),
                SigningCredentials = new SigningCredentials(_key, SecurityAlgorithms.RsaSha256),
            };
            var idToken = new JsonWebTokenHandler().CreateToken(descriptor);
            return Results.Json(new { id_token = idToken, access_token = "opaque", token_type = "Bearer", expires_in = 300 });
        });
    }

    private static bool PkceMatches(string verifier, string challenge) =>
        !string.IsNullOrEmpty(verifier)
        && Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))) == challenge;

    public async ValueTask DisposeAsync()
    {
        await _app.DisposeAsync();
        _rsa.Dispose();
    }
}
