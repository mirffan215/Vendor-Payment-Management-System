using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Vpms.TestSupport;

namespace Vpms.ApiTests;

[Collection(SqlServerCollection.Name)]
public sealed class AuditEvidenceApiTests : IClassFixture<ApiFixtureHolder>
{
    private static readonly Guid Maker = Guid.NewGuid();
    private readonly ApiFixture _api;

    public AuditEvidenceApiTests(ApiFixtureHolder holder) => _api = holder.Api;

    [Fact]
    public async Task Evidence_needs_its_own_permission_and_a_downloaded_bundle_verifies_over_http()
    {
        var plain = await SignIn("ev-plain@acme.com", "Auditor");
        await _api.AssignRoleAsync(_api.TenantA, "ev-plain@acme.com", "Auditor", null);
        var none = await SignIn("ev-none@acme.com");
        Assert.Equal(HttpStatusCode.Forbidden, (await none.Client.GetAsync("/api/v1/audit/evidence")).StatusCode);

        var response = await plain.Client.GetAsync("/api/v1/audit/evidence");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/zip", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal(64, response.Headers.GetValues("X-Manifest-SHA256").Single().Length);
        var bytes = await response.Content.ReadAsByteArrayAsync();

        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        form.Add(file, "file", "bundle.zip");
        var verify = await plain.Post("/api/v1/audit/evidence/verify", form);
        var body = await verify.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("isValid").GetBoolean(), body.ToString());

        bytes[bytes.Length / 2] ^= 0xFF; // damage the archive
        using var damaged = new MultipartFormDataContent { { new ByteArrayContent(bytes), "file", "bundle.zip" } };
        var bad = await (await plain.Post("/api/v1/audit/evidence/verify", damaged)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(bad.GetProperty("isValid").GetBoolean());
        using var again = new MultipartFormDataContent { { new ByteArrayContent(bytes), "file", "bundle.zip" } };
        Assert.Equal(HttpStatusCode.Forbidden, (await none.Post("/api/v1/audit/evidence/verify", again)).StatusCode);
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

        public Task<HttpResponseMessage> Post(string url, HttpContent content)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
            request.Headers.Add("X-XSRF-TOKEN", Csrf);
            return Client.SendAsync(request);
        }

        public Task<HttpResponseMessage> Put(string url, object body) => Send(HttpMethod.Put, url, body);
    }
}
