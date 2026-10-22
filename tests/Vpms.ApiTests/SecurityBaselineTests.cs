using System.Net;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;
using Vpms.Api.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Vpms.Api.Auth;
using Vpms.BuildingBlocks.Cloud;
using Vpms.TestSupport;

namespace Vpms.ApiTests;

/// <summary>Production start-up guards, cookie attributes, no cross-origin access, forwarded-header trust and request limits (ASVS V3, V9, V12, V13, V14).</summary>
public sealed class ProductionGuardTests
{
    private static Dictionary<string, string?> Safe() => new()
    {
        ["Encryption:Provider"] = "KeyVault",
        ["Storage:Provider"] = "Azure",
        ["Documents:Scanner"] = "ClamAv",
        ["DataProtection:Provider"] = "AzureBlob",
        ["DataProtection:KeyVaultKeyId"] = "https://v.vault.azure.net/keys/dp/1",
        ["KeyVault:Uri"] = "https://v.vault.azure.net/",
        ["AllowedHosts"] = "vpms.example.com",
        ["App:BaseUrl"] = "https://vpms.example.com",
        ["ForwardedHeaders:TrustImmediatePeer"] = "true",
        ["ConnectionStrings:Default"] = "Server=tcp:sql.example.net;Database=Vpms;Authentication=Active Directory Default;Encrypt=True",
    };

    private static IReadOnlyList<string> Check(Action<Dictionary<string, string?>>? change = null, bool web = true)
    {
        var settings = Safe();
        change?.Invoke(settings);
        return ProductionGuards.Problems(new ConfigurationBuilder().AddInMemoryCollection(settings).Build(), web);
    }

    [Fact]
    public void A_complete_production_configuration_has_no_problems() => Assert.Empty(Check());

    [Fact]
    public void Development_defaults_are_refused_in_production()
    {
        var problems = ProductionGuards.Problems(new ConfigurationBuilder().Build());
        Assert.True(problems.Count >= 7, string.Join(" | ", problems));
    }

    [Theory]
    [InlineData("Encryption:Provider", "Local", "Encryption:Provider")]
    [InlineData("Storage:Provider", "InMemory", "Storage:Provider")]
    [InlineData("Documents:Scanner", "Eicar", "Documents:Scanner")]
    [InlineData("DataProtection:Provider", "Default", "DataProtection:Provider")]
    [InlineData("AllowedHosts", "*", "AllowedHosts")]
    [InlineData("Auth:AllowInsecureMetadata", "true", "AllowInsecureMetadata")]
    [InlineData("Notifications:AllowInternalHosts", "true", "AllowInternalHosts")]
    [InlineData("ConnectionStrings:Default", "Server=s;Database=d;TrustServerCertificate=True", "certificate")]
    [InlineData("ConnectionStrings:Default", "Server=s;Database=d;Encrypt=False", "encrypt")]
    public void Each_unsafe_setting_is_named(string key, string value, string mention)
    {
        var problems = Check(s => s[key] = value);
        Assert.Contains(problems, p => p.Contains(mention, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void The_key_ring_must_be_encrypted_at_rest_and_the_ingress_must_be_named()
    {
        Assert.Contains(Check(s => s.Remove("DataProtection:KeyVaultKeyId")), p => p.Contains("KeyVaultKeyId"));
        Assert.Contains(Check(s => s.Remove("ForwardedHeaders:TrustImmediatePeer")), p => p.Contains("ForwardedHeaders"));
        Assert.Empty(Check(s => { s.Remove("ForwardedHeaders:TrustImmediatePeer"); s["ForwardedHeaders:KnownProxies:0"] = "10.0.0.4"; }));
    }

    [Fact]
    public void The_worker_is_checked_for_the_settings_it_uses_and_not_the_web_only_ones()
    {
        var worker = Check(s => { s.Remove("AllowedHosts"); s.Remove("DataProtection:Provider"); s.Remove("ForwardedHeaders:TrustImmediatePeer"); }, web: false);
        Assert.Empty(worker);
        Assert.Contains(Check(s => s["Documents:Scanner"] = "Eicar", web: false), p => p.Contains("Documents:Scanner"));
    }

    [Fact]
    public void Starting_in_production_with_problems_throws_and_lists_them()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Encryption:Provider"] = "Local" }).Build();
        var ex = Assert.Throws<InvalidOperationException>(() => ProductionGuards.EnsureSafe(config, new FakeEnvironment("Production")));
        Assert.Contains("Encryption:Provider", ex.Message);
        ProductionGuards.EnsureSafe(config, new FakeEnvironment("Development")); // other environments are not checked
    }

    private sealed class FakeEnvironment(string name) : Microsoft.Extensions.Hosting.IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "test";
        public string ContentRootPath { get; set; } = "/";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
}

[Collection(SqlServerCollection.Name)]
public sealed class SecurityBaselineTests : IClassFixture<ApiFixtureHolder>
{
    private readonly ApiFixture _api;

    public SecurityBaselineTests(ApiFixtureHolder holder) => _api = holder.Api;

    [Fact]
    public void Session_cookies_are_host_prefixed_secure_http_only_and_same_site()
    {
        var options = _api.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>();
        var staff = options.Get(VpmsAuth.CookieScheme).Cookie;
        var vendor = options.Get(VpmsAuth.VendorCookieScheme).Cookie;
        Assert.Equal("__Host-vpms", staff.Name);
        Assert.True(staff.HttpOnly);
        Assert.Equal(CookieSecurePolicy.Always, staff.SecurePolicy);
        Assert.Equal(SameSiteMode.Lax, staff.SameSite);
        Assert.Equal("__Host-vpms-vendor", vendor.Name);
        Assert.True(vendor.HttpOnly);
        Assert.Equal(CookieSecurePolicy.Always, vendor.SecurePolicy);
        Assert.Equal(SameSiteMode.Strict, vendor.SameSite);
        // lifetimes come from configuration, with these defaults
        Assert.Equal(TimeSpan.FromMinutes(20), options.Get(VpmsAuth.VendorCookieScheme).ExpireTimeSpan);
        Assert.Equal(TimeSpan.FromMinutes(30), options.Get(VpmsAuth.CookieScheme).ExpireTimeSpan);
        var portal = _api.Services.GetRequiredService<IOptions<Vpms.VendorPortal.Application.PortalOptions>>().Value;
        Assert.Equal(TimeSpan.FromHours(8), portal.SessionAbsoluteTimeout);
    }

    [Fact]
    public async Task No_other_origin_is_given_cross_origin_access()
    {
        var browser = _api.NewBrowser();
        var request = new HttpRequestMessage(HttpMethod.Options, "/api/v1/session");
        request.Headers.Add("Origin", "https://evil.example");
        request.Headers.Add("Access-Control-Request-Method", "GET");
        var preflight = await browser.SendAsync(request);
        Assert.False(preflight.Headers.Contains("Access-Control-Allow-Origin"));

        var get = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        get.Headers.Add("Origin", "https://evil.example");
        var response = await browser.SendAsync(get);
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.False(response.Headers.Contains("Access-Control-Allow-Credentials"));
    }

    [Theory]
    [InlineData("<script>alert(1)</script>", "failed")]
    [InlineData("anything at all", "failed")]
    [InlineData("UserDisabled", "UserDisabled")]
    public async Task The_sign_in_denied_page_echoes_only_known_reasons(string reason, string expected)
    {
        var response = await _api.NewBrowser().GetAsync("/auth/denied?reason=" + Uri.EscapeDataString(reason));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(expected, body.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task The_server_does_not_announce_what_it_runs()
    {
        var response = await _api.NewBrowser().GetAsync("/health/live");
        Assert.False(response.Headers.Contains("X-Powered-By"));
        Assert.False(response.Headers.Contains("X-AspNet-Version"));
    }

    [Fact]
    public void Forwarded_headers_are_off_unless_the_ingress_is_configured()
    {
        var o = _api.Services.GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value;
        Assert.Equal(ForwardedHeaders.None, o.ForwardedHeaders);
        Assert.Equal(1, o.ForwardLimit);
    }

    private static async Task<string> ClientAddressSeenAsync(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        using var host = await new HostBuilder().ConfigureWebHost(web => web
            .UseTestServer()
            .ConfigureServices(services => services.AddVpmsForwardedHeaders(configuration))
            .Configure(app =>
            {
                app.Use((ctx, next) => { ctx.Connection.RemoteIpAddress = IPAddress.Parse("198.51.100.7"); return next(ctx); });
                app.UseForwardedHeaders();
                app.Run(ctx => ctx.Response.WriteAsync(ctx.Connection.RemoteIpAddress + "|" + ctx.Request.Scheme));
            })).StartAsync();
        var client = host.GetTestClient();
        var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.Add("X-Forwarded-For", "203.0.113.9");
        request.Headers.Add("X-Forwarded-Proto", "https");
        return await (await client.SendAsync(request)).Content.ReadAsStringAsync();
    }

    [Fact]
    public async Task A_spoofed_forwarded_for_header_is_ignored_unless_the_sender_is_a_configured_proxy()
    {
        var ignored = await ClientAddressSeenAsync(new());
        Assert.DoesNotContain("203.0.113.9", ignored);
        Assert.EndsWith("|http", ignored);
        Assert.StartsWith("203.0.113.9|https", await ClientAddressSeenAsync(new() { ["ForwardedHeaders:TrustImmediatePeer"] = "true" }));
        // A named proxy only counts when it is the sender.
        Assert.DoesNotContain("203.0.113.9", await ClientAddressSeenAsync(new() { ["ForwardedHeaders:KnownProxies:0"] = "10.9.9.9" }));
        Assert.StartsWith("203.0.113.9|https", await ClientAddressSeenAsync(new() { ["ForwardedHeaders:KnownProxies:0"] = "198.51.100.7" }));
    }

    [Fact]
    public void Upload_and_request_limits_are_configured()
    {
        var limits = _api.Services.GetRequiredService<IOptions<Vpms.Api.Infrastructure.RequestLimitSettings>>().Value;
        Assert.Equal(1_048_576, limits.MaxRequestBodyBytes);
        Assert.Equal(8, limits.ConcurrentUploads);
        var form = _api.Services.GetRequiredService<IOptions<FormOptions>>().Value;
        Assert.Equal(limits.MaxMultipartBytes, form.MultipartBodyLengthLimit);
        var json = _api.Services.GetRequiredService<IOptions<Microsoft.AspNetCore.Mvc.JsonOptions>>().Value;
        Assert.Equal(32, json.JsonSerializerOptions.MaxDepth);
    }

    [Fact]
    public async Task Hsts_lasts_a_year_and_covers_subdomains_outside_development()
    {
        var hsts = _api.Services.GetRequiredService<IOptions<Microsoft.AspNetCore.HttpsPolicy.HstsOptions>>().Value;
        Assert.Equal(TimeSpan.FromDays(365), hsts.MaxAge);
        Assert.True(hsts.IncludeSubDomains);
        await Task.CompletedTask;
    }
}
