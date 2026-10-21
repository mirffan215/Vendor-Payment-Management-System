using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vpms.TestSupport;

namespace Vpms.ApiTests;

[Collection(SqlServerCollection.Name)]
public sealed class AuthenticationTests : IClassFixture<ApiFixtureHolder>
{
    private readonly ApiFixture _api;

    public AuthenticationTests(ApiFixtureHolder holder) => _api = holder.Api;

    [Fact]
    public async Task Unauthenticated_requests_get_401()
    {
        var browser = _api.NewBrowser();
        var response = await browser.GetAsync("/api/v1/session");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Provider_list_is_public_and_minimal()
    {
        var response = await _api.NewBrowser().GetAsync("/auth/providers?tenant=acme");
        var json = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Corporate SSO a", json);
        Assert.DoesNotContain("secret", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("client-a", json);
        Assert.DoesNotContain("Disabled", json);
        Assert.Single(JsonDocument.Parse(json).RootElement.EnumerateArray());
    }

    [Fact]
    public async Task Successful_login_creates_a_session_bound_to_the_providers_tenant()
    {
        var browser = _api.NewBrowser();
        _api.IdpA.NextUser = new FakeUser("sub-alice", "alice@acme.com", "Alice");

        var landing = await SignInAsync(browser, _api.IdpA, _api.ProviderA, "/dashboard");
        Assert.Equal("/dashboard", landing);

        var session = await browser.GetFromJsonAsync<JsonElement>("/api/v1/session");
        Assert.Equal(_api.TenantA.ToString(), session.GetProperty("tenantId").GetString());
        Assert.Equal("alice@acme.com", session.GetProperty("email").GetString());
        Assert.False(string.IsNullOrEmpty(session.GetProperty("csrfToken").GetString()));
    }

    [Fact]
    public async Task Tenant_claim_inside_the_external_token_is_ignored()
    {
        var browser = _api.NewBrowser();
        _api.IdpB.NextUser = new FakeUser("sub-mallory", "mallory@beta.com", "Mallory",
            ExtraClaims: new Dictionary<string, object> { ["tid"] = _api.TenantA.ToString(), ["vpms:tid"] = _api.TenantA.ToString() });

        await SignInAsync(browser, _api.IdpB, _api.ProviderB, "/");

        var session = await browser.GetFromJsonAsync<JsonElement>("/api/v1/session");
        Assert.Equal(_api.TenantB.ToString(), session.GetProperty("tenantId").GetString());
    }

    [Fact]
    public async Task Users_are_stored_only_in_their_own_tenant()
    {
        _api.IdpA.NextUser = new FakeUser("sub-bob", "bob@acme.com", "Bob");
        await SignInAsync(_api.NewBrowser(), _api.IdpA, _api.ProviderA, "/");

        var tenants = await _api.WithDbAsync(async (identity, _) =>
            await identity.Users.Where(u => u.ExternalSubject == "sub-bob").Select(u => u.TenantId).ToListAsync());
        Assert.Equal([_api.TenantA], tenants);
    }

    [Theory]
    [InlineData("issuer")]
    [InlineData("audience")]
    public async Task Tokens_with_wrong_issuer_or_audience_are_rejected(string defect)
    {
        var browser = _api.NewBrowser();
        _api.IdpA.NextUser = new FakeUser("sub-eve-" + defect, "eve@acme.com", "Eve",
            IssuerOverride: defect == "issuer" ? "https://evil.example/idp" : null,
            AudienceOverride: defect == "audience" ? "someone-else" : null);

        var location = await TryCompleteLoginAsync(browser, _api.IdpA, _api.ProviderA, "/");

        Assert.StartsWith("/auth/denied", location);
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync("/api/v1/session")).StatusCode);
        var created = await _api.WithDbAsync(async (identity, _) =>
            await identity.Users.AnyAsync(u => u.ExternalSubject == "sub-eve-" + defect));
        Assert.False(created);
    }

    [Fact]
    public async Task Email_domain_outside_the_allow_list_is_not_provisioned()
    {
        var browser = _api.NewBrowser();
        _api.IdpA.NextUser = new FakeUser("sub-outsider", "outsider@gmail.com", "Outsider");

        var location = await TryCompleteLoginAsync(browser, _api.IdpA, _api.ProviderA, "/");

        Assert.Contains("EmailDomainNotAllowed", location);
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync("/api/v1/session")).StatusCode);
    }

    [Fact]
    public async Task Unverified_email_cannot_be_used_to_auto_provision()
    {
        var browser = _api.NewBrowser();
        _api.IdpA.NextUser = new FakeUser("sub-unverified", "ceo@acme.com", "Fake CEO", EmailVerified: false);

        var location = await TryCompleteLoginAsync(browser, _api.IdpA, _api.ProviderA, "/");

        Assert.StartsWith("/auth/denied", location);
    }

    [Fact]
    public async Task Disabled_provider_cannot_start_a_login()
    {
        var response = await _api.NewBrowser().GetAsync($"/auth/login?provider={_api.DisabledProvider}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _api.NewBrowser().GetAsync($"/auth/login?provider={Guid.NewGuid()}")).StatusCode);
    }

    [Theory]
    [InlineData("//evil.example/x")]
    [InlineData("https://evil.example/x")]
    [InlineData("/\\evil.example")]
    public async Task Return_url_must_be_local(string returnUrl)
    {
        var browser = _api.NewBrowser();
        _api.IdpA.NextUser = new FakeUser("sub-redirect-" + returnUrl.GetHashCode(StringComparison.Ordinal), "r@acme.com", "R");
        var landing = await SignInAsync(browser, _api.IdpA, _api.ProviderA, returnUrl);
        Assert.Equal("/", landing);
    }

    [Fact]
    public async Task State_changing_calls_need_an_antiforgery_token_and_logout_ends_the_session()
    {
        var browser = _api.NewBrowser();
        _api.IdpA.NextUser = new FakeUser("sub-carol", "carol@acme.com", "Carol");
        await SignInAsync(browser, _api.IdpA, _api.ProviderA, "/");
        var session = await browser.GetFromJsonAsync<JsonElement>("/api/v1/session");

        var noToken = await browser.PostAsync("/auth/logout", null);
        Assert.Equal(HttpStatusCode.BadRequest, noToken.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/api/v1/session")).StatusCode);

        var request = new HttpRequestMessage(HttpMethod.Post, "/auth/logout");
        request.Headers.Add("X-XSRF-TOKEN", session.GetProperty("csrfToken").GetString());
        var ok = await browser.SendAsync(request);
        Assert.Equal(HttpStatusCode.NoContent, ok.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync("/api/v1/session")).StatusCode);
    }

    [Fact]
    public async Task Session_expires_after_the_absolute_lifetime()
    {
        var browser = _api.NewBrowser();
        _api.IdpA.NextUser = new FakeUser("sub-dave", "dave@acme.com", "Dave");
        await SignInAsync(browser, _api.IdpA, _api.ProviderA, "/");
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/api/v1/session")).StatusCode);

        _api.Clock.Advance(TimeSpan.FromHours(9));

        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync("/api/v1/session")).StatusCode);
    }

    [Fact]
    public async Task Disabled_users_cannot_sign_in()
    {
        _api.IdpA.NextUser = new FakeUser("sub-frank", "frank@acme.com", "Frank");
        await SignInAsync(_api.NewBrowser(), _api.IdpA, _api.ProviderA, "/");
        await _api.WithDbAsync(async (identity, _) =>
        {
            await identity.Users.Where(u => u.ExternalSubject == "sub-frank")
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.Status, "Disabled"));
            return 0;
        });

        var browser = _api.NewBrowser();
        var location = await TryCompleteLoginAsync(browser, _api.IdpA, _api.ProviderA, "/");

        Assert.Contains("UserDisabled", location);
    }

    // --- helpers -------------------------------------------------------------------------------------------

    /// <summary>Drives login -> IdP authorize -> callback by hand (no auto redirects) and returns the final location.</summary>
    private static async Task<string> TryCompleteLoginAsync(HttpClient browser, FakeOidcProvider idp, Guid providerId, string returnUrl)
    {
        var login = await browser.GetAsync($"/auth/login?provider={providerId}&returnUrl={Uri.EscapeDataString(returnUrl)}");
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        var authorizeUrl = login.Headers.Location!;
        Assert.StartsWith(idp.Authority, authorizeUrl.GetLeftPart(UriPartial.Path), StringComparison.Ordinal);
        Assert.Contains("code_challenge_method=S256", authorizeUrl.Query, StringComparison.Ordinal);

        using var idpClient = new HttpClient(idp.Handler, disposeHandler: false) { BaseAddress = new Uri("https://idp.test") };
        var authorize = await idpClient.GetAsync(authorizeUrl.PathAndQuery);
        Assert.Equal(HttpStatusCode.Redirect, authorize.StatusCode);
        var callback = authorize.Headers.Location!;

        var done = await browser.GetAsync(callback.PathAndQuery);
        return done.Headers.Location?.OriginalString ?? throw new InvalidOperationException($"No redirect: {(int)done.StatusCode}");
    }

    private static async Task<string> SignInAsync(HttpClient browser, FakeOidcProvider idp, Guid providerId, string returnUrl)
    {
        var location = await TryCompleteLoginAsync(browser, idp, providerId, returnUrl);
        Assert.DoesNotContain("/auth/denied", location, StringComparison.Ordinal);
        return location;
    }
}

public sealed class ApiFixtureHolder : IAsyncLifetime
{
    private readonly SqlServerFixture _sql = new();
    public ApiFixture Api { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _sql.InitializeAsync();
        Api = new ApiFixture(_sql);
        await Api.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        await Api.DisposeAsync();
        await _sql.DisposeAsync();
    }
}
