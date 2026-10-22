using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Vpms.TestSupport;

namespace Vpms.ApiTests;

[Collection(SqlServerCollection.Name)]
public sealed class NotificationsApiTests : IClassFixture<ApiFixtureHolder>
{
    private static readonly Guid Maker = Guid.NewGuid();
    private readonly ApiFixture _api;

    public NotificationsApiTests(ApiFixtureHolder holder) => _api = holder.Api;

    [Fact]
    public async Task Everyone_has_an_inbox_and_preferences_and_mandatory_ones_cannot_be_switched_off()
    {
        var me = await SignIn("n-user@acme.com");
        Assert.Equal(0, (await me.Client.GetFromJsonAsync<JsonElement>("/api/v1/notifications/unread-count")).GetProperty("count").GetInt32());
        Assert.Equal(JsonValueKind.Array, (await me.Client.GetFromJsonAsync<JsonElement>("/api/v1/notifications?locale=ar")).ValueKind);
        var prefs = await me.Client.GetFromJsonAsync<JsonElement>("/api/v1/notifications/preferences");
        Assert.Contains(prefs.EnumerateArray(), p => p.GetProperty("kind").GetString() == "ApprovalAssigned" && p.GetProperty("mandatory").GetBoolean());

        Assert.Equal("notification_mandatory", await ErrorCode(await me.Put("/api/v1/notifications/preferences", new { kind = "ApprovalAssigned", channel = "InApp", enabled = false })));
        var off = await me.Put("/api/v1/notifications/preferences", new { kind = "PaymentReleased", channel = "Email", enabled = false });
        Assert.Equal(HttpStatusCode.OK, off.StatusCode);
        var after = await me.Client.GetFromJsonAsync<JsonElement>("/api/v1/notifications/preferences");
        Assert.Contains(after.EnumerateArray(), p => p.GetProperty("kind").GetString() == "PaymentReleased" && p.GetProperty("channel").GetString() == "Email" && !p.GetProperty("enabled").GetBoolean());
        Assert.Equal(HttpStatusCode.NotFound, (await me.Post($"/api/v1/notifications/{Guid.NewGuid()}/read", new { })).StatusCode);
    }

    [Fact]
    public async Task Changing_the_wording_needs_its_own_permission_and_rejects_unknown_placeholders()
    {
        var plain = await SignIn("n-plain@acme.com");
        Assert.Equal(HttpStatusCode.Forbidden, (await plain.Client.GetAsync("/api/v1/admin/notification-templates")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await plain.Put("/api/v1/admin/notification-templates", new { kind = "ApprovalAssigned", channel = "Email", locale = "en", subject = "x", body = "y" })).StatusCode);

        var admin = await SignIn("n-admin@acme.com", "Notification Administrator");
        var list = await admin.Client.GetFromJsonAsync<JsonElement>("/api/v1/admin/notification-templates");
        Assert.True(list.GetArrayLength() >= 9 * 3 * 2);
        Assert.Equal("unknown_placeholder", await ErrorCode(await admin.Put("/api/v1/admin/notification-templates", new { kind = "ApprovalAssigned", channel = "Email", locale = "en", subject = "{{password}}", body = "y" })));
        var ok = await admin.Put("/api/v1/admin/notification-templates", new { kind = "ApprovalAssigned", channel = "Email", locale = "en", subject = "Decide: {{subject}}", body = "Open {{url}}" });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var preview = await (await admin.Post("/api/v1/admin/notification-templates/preview", new { kind = "ApprovalAssigned", channel = "Email", locale = "ar", subject = "{{subject}}", body = "{{url}}" })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("شركة الأمل", preview.GetProperty("subject").GetString());
    }

    // --- helpers -------------------------------------------------------------------------------------------

    private Task<Guid> Entity(string code) => ActiveVendorKit.CreateEntityAsync(_api.Services, _api.TenantA, Maker, code);

    private async Task<Session> SignIn(string email, string? role = null)
    {
        var client = _api.NewBrowser();
        _api.IdpA.NextUser = new FakeUser("sub-" + email, email, email.Split('@')[0]);
        await AuthenticationTests.SignInAsync(client, _api.IdpA, _api.ProviderA, "/");
        if (role is not null) await _api.AssignRoleAsync(_api.TenantA, email, role, null);
        var me = await client.GetFromJsonAsync<JsonElement>("/api/v1/session");
        return new Session(client, me.GetProperty("csrfToken").GetString()!, Guid.Parse(me.GetProperty("userId").GetString()!));
    }

    private static async Task<string?> ErrorCode(HttpResponseMessage r) => (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errorCode").GetString();

    private sealed record Session(HttpClient Client, string Csrf, Guid UserId)
    {
        private Task<HttpResponseMessage> Send(HttpMethod m, string url, object body)
        {
            var request = new HttpRequestMessage(m, url) { Content = JsonContent.Create(body) };
            request.Headers.Add("X-XSRF-TOKEN", Csrf);
            return Client.SendAsync(request);
        }

        public Task<HttpResponseMessage> Post(string url, object body) => Send(HttpMethod.Post, url, body);

        public Task<HttpResponseMessage> Put(string url, object body) => Send(HttpMethod.Put, url, body);
    }
}
