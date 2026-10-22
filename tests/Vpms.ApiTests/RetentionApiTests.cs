using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Vpms.TestSupport;

namespace Vpms.ApiTests;

[Collection(SqlServerCollection.Name)]
public sealed class RetentionApiTests : IClassFixture<ApiFixtureHolder>
{
    private readonly ApiFixture _api;

    public RetentionApiTests(ApiFixtureHolder holder) => _api = holder.Api;

    private sealed record Session(HttpClient Client, string Csrf)
    {
        public Task<HttpResponseMessage> Send(HttpMethod method, string url, object? body = null)
        {
            var request = new HttpRequestMessage(method, url) { Content = body is null ? null : JsonContent.Create(body) };
            request.Headers.Add("X-XSRF-TOKEN", Csrf);
            return Client.SendAsync(request);
        }

        public Task<HttpResponseMessage> Post(string url, object? body = null) => Send(HttpMethod.Post, url, body ?? new { });
    }

    private async Task<Session> SignInAsync(string email, string? role)
    {
        var browser = _api.NewBrowser();
        _api.IdpA.NextUser = new FakeUser("sub-" + email, email, email.Split('@')[0]);
        await AuthenticationTests.SignInAsync(browser, _api.IdpA, _api.ProviderA, "/");
        if (role is not null) await _api.AssignRoleAsync(_api.TenantA, email, role, null);
        var csrf = (await browser.GetFromJsonAsync<JsonElement>("/api/v1/session")).GetProperty("csrfToken").GetString()!;
        return new Session(browser, csrf);
    }

    private static object Policy(int notificationMonths) => new { settings = new { months = new Dictionary<string, int> { ["Notifications"] = notificationMonths }, countries = new Dictionary<string, object>() }, note = "api test" };

    [Fact]
    public async Task Nothing_here_is_open_to_a_person_without_the_retention_permissions()
    {
        var nobody = await SignInAsync("ret-nobody@acme.com", role: null);
        Assert.Equal(HttpStatusCode.Forbidden, (await nobody.Send(HttpMethod.Get, "/api/v1/admin/retention")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await nobody.Send(HttpMethod.Get, "/api/v1/admin/retention/report")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await nobody.Send(HttpMethod.Put, "/api/v1/admin/retention", Policy(12))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await nobody.Send(HttpMethod.Get, "/api/v1/admin/legal-holds")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await nobody.Post("/api/v1/admin/legal-holds", new { scope = "Tenant", reason = "x" })).StatusCode);
    }

    [Fact]
    public async Task A_policy_is_proposed_by_one_person_and_approved_by_another()
    {
        var maker = await SignInAsync("ret-admin@acme.com", "Retention Administrator");
        var checker = await SignInAsync("ret-approver@acme.com", "Retention Policy Approver");

        var proposed = await maker.Send(HttpMethod.Put, "/api/v1/admin/retention", Policy(18));
        Assert.Equal(HttpStatusCode.OK, proposed.StatusCode);
        var id = (await proposed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        // the maker cannot approve (no permission), and the approver cannot propose
        Assert.Equal(HttpStatusCode.Forbidden, (await maker.Post($"/api/v1/admin/retention/{id}/approve", new { comment = "me" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await checker.Send(HttpMethod.Put, "/api/v1/admin/retention", Policy(30))).StatusCode);

        // both can see the pending change; only the approver can decide it
        var state = await checker.Client.GetFromJsonAsync<JsonElement>("/api/v1/admin/retention");
        Assert.Equal(id, state.GetProperty("pending").GetProperty("id").GetGuid());
        // approving is a high-risk action: an old sign-in is not enough, a fresh one is
        _api.Clock.Advance(TimeSpan.FromMinutes(20));
        var stale = await checker.Post($"/api/v1/admin/retention/{id}/approve", new { comment = "late" });
        Assert.Equal(HttpStatusCode.Forbidden, stale.StatusCode);
        Assert.Equal("step_up_required", (await stale.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errorCode").GetString());
        checker = await SignInAsync("ret-approver@acme.com", role: null);
        Assert.Equal(HttpStatusCode.OK, (await checker.Post($"/api/v1/admin/retention/{id}/approve", new { comment = "ok" })).StatusCode);
        var after = await maker.Client.GetFromJsonAsync<JsonElement>("/api/v1/admin/retention");
        Assert.Equal(18, after.GetProperty("active").GetProperty("settings").GetProperty("months").GetProperty("Notifications").GetInt32());

        var report = await maker.Client.GetFromJsonAsync<JsonElement>("/api/v1/admin/retention/report");
        var notifications = report.EnumerateArray().Single(r => r.GetProperty("dataClass").GetString() == "Notifications");
        Assert.Equal(18, notifications.GetProperty("months").GetInt32());
        Assert.True(notifications.GetProperty("disposedAutomatically").GetBoolean());
    }

    [Fact]
    public async Task A_hold_is_placed_by_one_person_and_released_by_another()
    {
        var placer = await SignInAsync("hold-a@acme.com", "Retention Administrator");
        var releaser = await SignInAsync("hold-b@acme.com", "Retention Administrator");
        var placed = await placer.Post("/api/v1/admin/legal-holds", new { scope = "Tenant", reason = "Regulator enquiry" });
        Assert.Equal(HttpStatusCode.OK, placed.StatusCode);
        var id = (await placed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var self = await placer.Post($"/api/v1/admin/legal-holds/{id}/release", new { comment = "mine" });
        Assert.Equal(HttpStatusCode.Forbidden, self.StatusCode); // second person required
        Assert.Equal(HttpStatusCode.OK, (await releaser.Post($"/api/v1/admin/legal-holds/{id}/release", new { comment = "Enquiry closed" })).StatusCode);
        var active = await placer.Client.GetFromJsonAsync<JsonElement>("/api/v1/admin/legal-holds?activeOnly=true");
        Assert.DoesNotContain(active.EnumerateArray(), h => h.GetProperty("id").GetGuid() == id);
    }
}
