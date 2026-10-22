using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Vpms.TestSupport;

namespace Vpms.ApiTests;

/// <summary>Re-authentication for high-risk actions and ending sessions on demand (ASVS V2, V3).</summary>
[Collection(SqlServerCollection.Name)]
public sealed class StepUpAndSessionTests : IClassFixture<ApiFixtureHolder>
{
    private readonly ApiFixture _api;

    public StepUpAndSessionTests(ApiFixtureHolder holder) => _api = holder.Api;

    private async Task<(HttpClient Browser, string Csrf)> SignInAsync(string email, string? role = "Tenant Administrator")
    {
        var browser = _api.NewBrowser();
        _api.IdpA.NextUser = new FakeUser("sub-" + email, email, email.Split('@')[0]);
        await AuthenticationTests.SignInAsync(browser, _api.IdpA, _api.ProviderA, "/");
        if (role is not null) await _api.AssignRoleAsync(_api.TenantA, email, role, null);
        return (browser, await CsrfAsync(browser));
    }

    private static async Task<string> CsrfAsync(HttpClient browser) =>
        (await browser.GetFromJsonAsync<JsonElement>("/api/v1/session")).GetProperty("csrfToken").GetString()!;

    private static Task<HttpResponseMessage> Post(HttpClient browser, string csrf, string url, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        request.Headers.Add("X-XSRF-TOKEN", csrf);
        return browser.SendAsync(request);
    }

    private static async Task<string> RunStepUpAsync(HttpClient browser, FakeOidcProvider idp, string returnUrl)
    {
        var start = await browser.GetAsync("/auth/step-up?returnUrl=" + Uri.EscapeDataString(returnUrl));
        Assert.Equal(HttpStatusCode.Redirect, start.StatusCode);
        using var idpClient = new HttpClient(idp.Handler, disposeHandler: false) { BaseAddress = new Uri("https://idp.test") };
        var authorize = await idpClient.GetAsync(start.Headers.Location!.PathAndQuery);
        var done = await browser.GetAsync(authorize.Headers.Location!.PathAndQuery);
        return done.Headers.Location?.OriginalString ?? throw new InvalidOperationException($"No redirect: {(int)done.StatusCode}");
    }

    [Fact]
    public async Task A_high_risk_action_needs_a_recent_sign_in_and_the_step_up_flow_restores_it()
    {
        var (browser, csrf) = await SignInAsync("stepup-admin@acme.com");
        var role = new { name = "Step Up Role A", permissions = new[] { "Vendor.View" } };
        Assert.Equal(HttpStatusCode.OK, (await Post(browser, csrf, "/api/v1/admin/roles", role)).StatusCode);

        _api.Clock.Advance(TimeSpan.FromMinutes(20));
        var stale = await Post(browser, csrf, "/api/v1/admin/roles", new { name = "Step Up Role B", permissions = new[] { "Vendor.View" } });
        Assert.Equal(HttpStatusCode.Forbidden, stale.StatusCode);
        Assert.Equal("application/problem+json", stale.Content.Headers.ContentType?.MediaType);
        var problem = await stale.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("step_up_required", problem.GetProperty("errorCode").GetString());
        Assert.Equal("/auth/step-up", problem.GetProperty("stepUpUrl").GetString());

        // ordinary actions are not affected by an old sign-in
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/api/v1/session")).StatusCode);

        var landing = await RunStepUpAsync(browser, _api.IdpA, "/admin");
        Assert.Equal("/admin", landing);
        Assert.Contains("prompt=login", _api.IdpA.LastAuthorizeQuery);
        Assert.Contains("max_age=0", _api.IdpA.LastAuthorizeQuery);

        var fresh = await CsrfAsync(browser);
        Assert.Equal(HttpStatusCode.OK, (await Post(browser, fresh, "/api/v1/admin/roles", new { name = "Step Up Role B", permissions = new[] { "Vendor.View" } })).StatusCode);
    }

    [Fact]
    public async Task A_provider_that_does_not_prove_a_fresh_sign_in_does_not_satisfy_the_step_up()
    {
        var (browser, csrf) = await SignInAsync("stepup-ignored@acme.com");
        _api.Clock.Advance(TimeSpan.FromMinutes(20));
        _api.IdpA.IgnoreMaxAge = true;
        try
        {
            var landing = await RunStepUpAsync(browser, _api.IdpA, "/admin");
            Assert.StartsWith("/auth/denied", landing, StringComparison.Ordinal);
            Assert.Contains("StepUpNotPerformed", landing, StringComparison.Ordinal);
        }
        finally
        {
            _api.IdpA.IgnoreMaxAge = false;
        }

        var stillStale = await Post(browser, csrf, "/api/v1/admin/roles", new { name = "Never Created", permissions = new[] { "Vendor.View" } });
        Assert.Equal(HttpStatusCode.Forbidden, stillStale.StatusCode);
        Assert.Equal("step_up_required", (await stillStale.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task Step_up_is_only_for_signed_in_people_and_never_redirects_off_site()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await _api.NewBrowser().GetAsync("/auth/step-up")).StatusCode);
        var (browser, _) = await SignInAsync("stepup-redirect@acme.com", role: null);
        var start = await browser.GetAsync("/auth/step-up?returnUrl=" + Uri.EscapeDataString("https://evil.example/steal"));
        Assert.Equal(HttpStatusCode.Redirect, start.StatusCode);
        // the return address is a local path: the provider is sent the callback address, and the redirect after sign-in is "/"
        using var idpClient = new HttpClient(_api.IdpA.Handler, disposeHandler: false) { BaseAddress = new Uri("https://idp.test") };
        var authorize = await idpClient.GetAsync(start.Headers.Location!.PathAndQuery);
        var done = await browser.GetAsync(authorize.Headers.Location!.PathAndQuery);
        Assert.Equal("/", done.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task Signing_out_everywhere_ends_every_session_of_the_person_and_a_new_sign_in_works()
    {
        var (first, csrf) = await SignInAsync("everywhere@acme.com", role: null);
        var second = _api.NewBrowser();
        _api.IdpA.NextUser = new FakeUser("sub-everywhere@acme.com", "everywhere@acme.com", "everywhere");
        await AuthenticationTests.SignInAsync(second, _api.IdpA, _api.ProviderA, "/");
        Assert.Equal(HttpStatusCode.OK, (await second.GetAsync("/api/v1/session")).StatusCode);

        _api.Clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(HttpStatusCode.NoContent, (await Post(first, csrf, "/api/v1/session/revoke-all", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await first.GetAsync("/api/v1/session")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await second.GetAsync("/api/v1/session")).StatusCode);

        _api.Clock.Advance(TimeSpan.FromSeconds(5));
        var again = _api.NewBrowser();
        _api.IdpA.NextUser = new FakeUser("sub-everywhere@acme.com", "everywhere@acme.com", "everywhere");
        await AuthenticationTests.SignInAsync(again, _api.IdpA, _api.ProviderA, "/");
        Assert.Equal(HttpStatusCode.OK, (await again.GetAsync("/api/v1/session")).StatusCode);
    }

    [Fact]
    public async Task An_administrator_can_end_another_persons_sessions_and_it_is_audited()
    {
        var (admin, adminCsrf) = await SignInAsync("revoker@acme.com");
        var (victim, _) = await SignInAsync("victim@acme.com", role: null);
        var users = await admin.GetFromJsonAsync<JsonElement>("/api/v1/admin/users?search=victim");
        var victimId = users[0].GetProperty("id").GetGuid();

        _api.Clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(HttpStatusCode.NoContent, (await Post(admin, adminCsrf, $"/api/v1/admin/users/{victimId}/revoke-sessions", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await victim.GetAsync("/api/v1/session")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/v1/session")).StatusCode);

        var events = await admin.GetFromJsonAsync<JsonElement>("/api/v1/audit/events?action=user.sessions.revoked");
        Assert.Contains(events.GetProperty("items").EnumerateArray(), e => e.GetProperty("entityId").GetString() == victimId.ToString());
    }

    [Fact]
    public async Task A_person_without_the_user_permission_cannot_end_someone_elses_sessions()
    {
        var (plain, csrf) = await SignInAsync("nobody-revoke@acme.com", role: null);
        Assert.Equal(HttpStatusCode.Forbidden, (await Post(plain, csrf, $"/api/v1/admin/users/{Guid.NewGuid()}/revoke-sessions", new { })).StatusCode);
    }
}
