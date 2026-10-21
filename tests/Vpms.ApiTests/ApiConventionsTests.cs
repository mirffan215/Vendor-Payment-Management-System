using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Vpms.Api.Infrastructure;
using Vpms.BuildingBlocks.Security;
using Vpms.BuildingBlocks.Tenancy;
using Vpms.TestSupport;

namespace Vpms.ApiTests;

[Collection(SqlServerCollection.Name)]
public sealed class ApiConventionsTests : IClassFixture<ApiFixtureHolder>
{
    private readonly ApiFixture _api;

    public ApiConventionsTests(ApiFixtureHolder holder) => _api = holder.Api;

    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    public async Task Health_endpoints_are_anonymous_and_reveal_nothing(string path)
    {
        var response = await _api.NewBrowser().GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Unauthenticated_response_is_a_problem_details_document()
    {
        var response = await _api.NewBrowser().GetAsync("/api/v1/audit/events");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(401, body.GetProperty("status").GetInt32());
        Assert.Equal("unauthenticated", body.GetProperty("errorCode").GetString());
        Assert.False(string.IsNullOrEmpty(body.GetProperty("traceId").GetString()));
        Assert.False(string.IsNullOrEmpty(body.GetProperty("correlationId").GetString()));
    }

    [Fact]
    public async Task Forbidden_response_is_a_problem_details_document()
    {
        var browser = await SignInAsync("conv-noperm@acme.com");
        var response = await browser.GetAsync("/api/v1/audit/events");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(403, body.GetProperty("status").GetInt32());
        Assert.True(body.TryGetProperty("errorCode", out _));
    }

    [Fact]
    public async Task Api_is_versioned_in_the_url_and_reports_supported_versions()
    {
        var browser = await SignInAsync("conv-version@acme.com");

        var v1 = await browser.GetAsync("/api/v1/session");
        Assert.Equal(HttpStatusCode.OK, v1.StatusCode);
        Assert.Equal("1.0", v1.Headers.GetValues("api-supported-versions").Single());

        // An API version that does not exist has no route, so it is a problem-details 404.
        var v2 = await browser.GetAsync("/api/v2/session");
        Assert.Equal(HttpStatusCode.NotFound, v2.StatusCode);
        Assert.Equal("application/problem+json", v2.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Responses_carry_security_headers_and_authenticated_ones_are_not_cached()
    {
        var browser = await SignInAsync("conv-headers@acme.com");
        var response = await browser.GetAsync("/api/v1/session");
        var h = response.Headers;
        Assert.Equal("nosniff", h.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", h.GetValues("X-Frame-Options").Single());
        Assert.Equal("no-referrer", h.GetValues("Referrer-Policy").Single());
        Assert.Contains("frame-ancestors 'none'", h.GetValues("Content-Security-Policy").Single(), StringComparison.Ordinal);
        Assert.True(response.Headers.CacheControl?.NoStore);
    }

    [Fact]
    public async Task OpenApi_document_is_published()
    {
        var response = await _api.NewBrowser().GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.True(doc.TryGetProperty("openapi", out _));
        Assert.Contains("/api/v{version}/audit/events", doc.GetProperty("paths").EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public async Task Request_logs_carry_tenant_user_and_correlation_ids()
    {
        var browser = await SignInAsync("conv-logs@acme.com");
        var correlation = "log-" + Guid.NewGuid().ToString("N");
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/session");
        request.Headers.Add("X-Correlation-Id", correlation);
        await browser.SendAsync(request);

        var logged = _api.LogSink.Events.Where(e => e.Properties.TryGetValue("CorrelationId", out var c) && c.ToString().Contains(correlation, StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(logged);
        Assert.Contains(logged, e => e.Properties["TenantId"].ToString().Contains(_api.TenantA.ToString(), StringComparison.Ordinal));
        Assert.Contains(logged, e => e.Properties.ContainsKey("UserId"));
        // query strings (which hold OIDC codes) are never part of the request log
        Assert.DoesNotContain(_api.LogSink.Events, e => e.RenderMessage().Contains("code=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Rate_limiting_returns_429_problem_details_with_retry_after()
    {
        await using var limited = new LimitedApi(_api);
        var client = limited.NewBrowser();

        HttpResponseMessage? rejected = null;
        for (var i = 0; i < 8 && rejected is null; i++)
        {
            var response = await client.GetAsync("/auth/providers?tenant=acme");
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                rejected = response;
            }
        }

        Assert.NotNull(rejected);
        Assert.Equal("application/problem+json", rejected.Content.Headers.ContentType?.MediaType);
        Assert.True(rejected.Headers.Contains("Retry-After"));
        Assert.Equal("rate_limited", JsonDocument.Parse(await rejected.Content.ReadAsStringAsync()).RootElement.GetProperty("errorCode").GetString());
    }

    // --- exception mapping (unit level) --------------------------------------------------------------------

    [Fact]
    public async Task Segregation_of_duties_violations_map_to_403_with_the_rule()
    {
        var (status, body) = await Handle(new SegregationOfDutiesException("maker-checker", "Self-approval is not allowed for 'x'."));
        Assert.Equal(403, status);
        Assert.Equal("sod_violation", body.GetProperty("errorCode").GetString());
        Assert.Equal("maker-checker", body.GetProperty("rule").GetString());
    }

    [Fact]
    public async Task Tenant_violations_do_not_leak_details()
    {
        var (status, body) = await Handle(new TenantViolationException("Entity LegalEntity belongs to tenant 1234"));
        Assert.Equal(403, status);
        Assert.Equal("tenant_violation", body.GetProperty("errorCode").GetString());
        Assert.DoesNotContain("1234", body.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unexpected_errors_return_500_without_internal_details()
    {
        var (status, body) = await Handle(new InvalidOperationException("Password=hunter2; Server=prod-sql"));
        Assert.Equal(500, status);
        Assert.Equal("internal_error", body.GetProperty("errorCode").GetString());
        Assert.DoesNotContain("hunter2", body.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("prod-sql", body.ToString(), StringComparison.Ordinal);
    }

    private static async Task<(int Status, JsonElement Body)> Handle(Exception exception)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddProblemDetails(o => o.CustomizeProblemDetails = ProblemDetailsCustomization.Customize);
        services.AddSingleton<ProblemDetailsExceptionHandler>(sp => new ProblemDetailsExceptionHandler(
            NullLogger<ProblemDetailsExceptionHandler>.Instance,
            sp.GetRequiredService<IProblemDetailsService>()));
        await using var provider = services.BuildServiceProvider();

        var http = new DefaultHttpContext { RequestServices = provider };
        http.Response.Body = new MemoryStream();
        var handled = await provider.GetRequiredService<ProblemDetailsExceptionHandler>().TryHandleAsync(http, exception, default);
        Assert.True(handled);

        http.Response.Body.Position = 0;
        var body = await JsonDocument.ParseAsync(http.Response.Body);
        return (http.Response.StatusCode, body.RootElement.Clone());
    }

    private async Task<HttpClient> SignInAsync(string email)
    {
        var browser = _api.NewBrowser();
        _api.IdpA.NextUser = new FakeUser("sub-" + email, email, email.Split('@')[0]);
        await AuthenticationTests.SignInAsync(browser, _api.IdpA, _api.ProviderA, "/");
        return browser;
    }

    /// <summary>The same API, but with a tiny sign-in rate limit.</summary>
    private sealed class LimitedApi(ApiFixture parent) : IAsyncDisposable
    {
        private readonly WebApplicationFactoryHolder _holder = new(parent);

        public HttpClient NewBrowser() => _holder.Client;

        public ValueTask DisposeAsync() => _holder.DisposeAsync();
    }

    private sealed class WebApplicationFactoryHolder : IAsyncDisposable
    {
        private readonly Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> _factory;

        public WebApplicationFactoryHolder(ApiFixture parent) =>
            _factory = parent.WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, c) =>
                c.AddInMemoryCollection(new Dictionary<string, string?> { ["RateLimiting:AuthPermitLimit"] = "3" })));

        public HttpClient Client => _factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost"),
        });

        public ValueTask DisposeAsync() => _factory.DisposeAsync();
    }
}
