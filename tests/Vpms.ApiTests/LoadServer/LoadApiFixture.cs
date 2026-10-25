using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Vpms.Api.Auth;
using Vpms.TestSupport;

namespace Vpms.ApiTests.LoadServer;

/// <summary>
/// The real API on a real HTTPS port with a test-only door for load tests: <c>GET /_load/login/{persona}</c> signs the persona in without the identity-provider
/// round trip. The door exists only in this test host; the production code has no such endpoint.
/// </summary>
public sealed class LoadApiFixture : ApiFixture
{
    public Dictionary<string, (Guid UserId, string Email, string Name)> Personas { get; } = new();
    public Guid ProviderId => ProviderA;

    public LoadApiFixture(SqlServerFixture sql, int port) : base(sql)
    {
        var certificate = SelfSigned();
        UseKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, port, listen => listen.UseHttps(certificate)));
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        // The per-user limit (600 a minute) protects production; the load persona is one user, so it is lifted here and measured separately.
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?> { ["RateLimiting:PermitLimit"] = "10000000", ["RateLimiting:UploadSlots"] = "64" }));
        builder.ConfigureServices(services => services.AddTransient<IStartupFilter>(_ => new LoginDoor(Personas, TenantA, ProviderA, Clock)));
    }

    private static X509Certificate2 SelfSigned()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(san.Build());
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(7));
        return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx), null);
    }

    private sealed class LoginDoor(IReadOnlyDictionary<string, (Guid UserId, string Email, string Name)> personas, Guid tenantId, Guid providerId, TimeProvider clock) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (ctx, nextMiddleware) =>
            {
                const string prefix = "/_load/login/";
                if (!ctx.Request.Path.StartsWithSegments(prefix.TrimEnd('/'), out var rest) || !personas.TryGetValue(rest.Value!.Trim('/'), out var who))
                {
                    await nextMiddleware();
                    return;
                }

                var identity = new ClaimsIdentity(
                [
                    new Claim(VpmsClaims.UserId, who.UserId.ToString()),
                    new Claim(VpmsClaims.TenantId, tenantId.ToString()),
                    new Claim(VpmsClaims.ProviderId, providerId.ToString()),
                    new Claim(VpmsClaims.Context, VpmsClaims.InternalContext),
                    new Claim(VpmsClaims.AuthTime, clock.GetUtcNow().ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    new Claim("name", who.Name),
                    new Claim("email", who.Email),
                    new Claim("locale", "en"),
                ], "vpms", "name", "role");
                await ctx.SignInAsync(VpmsAuth.CookieScheme, new ClaimsPrincipal(identity));
                ctx.Response.StatusCode = StatusCodes.Status204NoContent;
            });
            next(app);
        };
    }
}
