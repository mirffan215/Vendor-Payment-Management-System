using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Vpms.TestSupport;

namespace Vpms.ApiTests;

[Collection(SqlServerCollection.Name)]
public sealed class AuditApiTests : IClassFixture<ApiFixtureHolder>
{
    private readonly ApiFixture _api;

    public AuditApiTests(ApiFixtureHolder holder) => _api = holder.Api;

    [Fact]
    public async Task Anonymous_callers_get_401()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await _api.NewBrowser().GetAsync("/api/v1/audit/events")).StatusCode);
    }

    [Fact]
    public async Task Signed_in_user_without_Audit_Read_gets_403()
    {
        var browser = await SignInAsync(_api.IdpA, _api.ProviderA, "noperm@acme.com");
        Assert.Equal(HttpStatusCode.Forbidden, (await browser.GetAsync("/api/v1/audit/events")).StatusCode);
    }

    [Fact]
    public async Task Tenant_auditor_sees_login_events_of_their_own_tenant_only()
    {
        var auditor = await SignInAsync(_api.IdpA, _api.ProviderA, "auditor@acme.com");
        await SignInAsync(_api.IdpB, _api.ProviderB, "someone@beta.com"); // generates events in tenant B
        await _api.AssignRoleAsync(_api.TenantA, "auditor@acme.com", "Auditor", null);

        var response = await auditor.GetAsync("/api/v1/audit/events?limit=200");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var items = body.GetProperty("items").EnumerateArray().ToList();

        Assert.Contains(items, i => i.GetProperty("action").GetString() == "auth.login.succeeded");
        Assert.Contains(items, i => i.GetProperty("action").GetString() == "user.provisioned");
        Assert.Contains(items, i => i.GetProperty("action").GetString() == "role.assigned");
        // Events raised by HTTP requests (sign-in) carry the request's correlation id and client address.
        Assert.All(items.Where(i => i.GetProperty("action").GetString()!.StartsWith("auth.", StringComparison.Ordinal)),
            i => Assert.False(string.IsNullOrEmpty(i.GetProperty("correlationId").GetString())));
        Assert.DoesNotContain(items, i => i.GetProperty("changeSummary").GetString()!.Contains("someone@beta.com", StringComparison.Ordinal));

        // sequences are strictly increasing: the caller's own tenant chain, not a mix of tenants
        var sequences = items.Select(i => i.GetProperty("sequence").GetInt64()).ToList();
        Assert.Equal(sequences.OrderBy(x => x), sequences);
        Assert.Equal(sequences.Distinct().Count(), sequences.Count);
    }

    [Fact]
    public async Task Entity_scoped_auditor_is_limited_to_their_entity()
    {
        var entity = Guid.NewGuid();
        var other = Guid.NewGuid();
        var browser = await SignInAsync(_api.IdpA, _api.ProviderA, "scoped@acme.com");
        await _api.AssignRoleAsync(_api.TenantA, "scoped@acme.com", "Auditor", entity);

        var all = await browser.GetFromJsonAsync<JsonElement>("/api/v1/audit/events");
        // tenant-level events (no legal entity) are not visible to an entity-scoped auditor
        Assert.All(all.GetProperty("items").EnumerateArray(), i =>
            Assert.Equal(entity.ToString(), i.GetProperty("legalEntityId").GetString()));

        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync($"/api/v1/audit/events?legalEntityId={entity}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await browser.GetAsync($"/api/v1/audit/events?legalEntityId={other}")).StatusCode);
    }

    [Fact]
    public async Task Denied_sign_in_is_audited_without_personal_data()
    {
        var auditor = await SignInAsync(_api.IdpA, _api.ProviderA, "auditor2@acme.com");
        await _api.AssignRoleAsync(_api.TenantA, "auditor2@acme.com", "Auditor", null);

        _api.IdpA.NextUser = new FakeUser("sub-intruder", "intruder@evil.com", "Intruder");
        var location = await AuthenticationTests.TryCompleteLoginAsync(_api.NewBrowser(), _api.IdpA, _api.ProviderA, "/");
        Assert.Contains("EmailDomainNotAllowed", location);

        var body = await auditor.GetFromJsonAsync<JsonElement>("/api/v1/audit/events?action=auth.login.denied&limit=200");
        var denied = body.GetProperty("items").EnumerateArray().Last();
        Assert.Equal("Anonymous", denied.GetProperty("actorType").GetString());
        var summary = denied.GetProperty("changeSummary").GetString()!;
        Assert.Contains("EmailDomainNotAllowed", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("intruder", summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Client_supplied_correlation_id_is_echoed_and_recorded_but_unsafe_ones_are_replaced()
    {
        var browser = await SignInAsync(_api.IdpA, _api.ProviderA, "corr@acme.com");
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/session");
        request.Headers.Add("X-Correlation-Id", "abc-123");
        Assert.Equal("abc-123", (await browser.SendAsync(request)).Headers.GetValues("X-Correlation-Id").Single());

        var evil = new HttpRequestMessage(HttpMethod.Get, "/api/v1/session");
        evil.Headers.Add("X-Correlation-Id", "x y<script>");
        Assert.NotEqual("x y<script>", (await browser.SendAsync(evil)).Headers.GetValues("X-Correlation-Id").Single());
    }

    private async Task<HttpClient> SignInAsync(FakeOidcProvider idp, Guid providerId, string email)
    {
        var browser = _api.NewBrowser();
        idp.NextUser = new FakeUser("sub-" + email, email, email.Split('@')[0]);
        await AuthenticationTests.SignInAsync(browser, idp, providerId, "/");
        return browser;
    }
}
