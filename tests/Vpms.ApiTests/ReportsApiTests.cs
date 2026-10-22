using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Vpms.Identity.Contracts;
using Vpms.TestSupport;

namespace Vpms.ApiTests;

[Collection(SqlServerCollection.Name)]
public sealed class ReportsApiTests : IClassFixture<ApiFixtureHolder>
{
    private static readonly Guid Maker = Guid.NewGuid();
    private readonly ApiFixture _api;

    public ReportsApiTests(ApiFixtureHolder holder) => _api = holder.Api;

    [Fact]
    public async Task Reports_need_a_permission_and_the_tax_report_needs_the_tax_permission()
    {
        var plain = await SignIn("rep-plain@acme.com");
        Assert.Equal(HttpStatusCode.Forbidden, (await plain.Client.GetAsync("/api/v1/reports")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await plain.Client.GetAsync("/api/v1/reports/payables-aging")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await plain.Client.GetAsync("/api/v1/reports/payables-aging/export")).StatusCode);

        var viewer = await SignIn("rep-viewer@acme.com", "Report Viewer");
        var catalog = await viewer.Client.GetFromJsonAsync<JsonElement>("/api/v1/reports");
        Assert.DoesNotContain(catalog.EnumerateArray(), r => r.GetProperty("key").GetString() == "input-vat-register");
        Assert.Contains(catalog.EnumerateArray(), r => r.GetProperty("key").GetString() == "payables-aging");
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.Client.GetAsync("/api/v1/reports/input-vat-register")).StatusCode);

        var tax = await SignIn("rep-tax@acme.com", "Tax Reviewer");
        Assert.Equal(HttpStatusCode.OK, (await tax.Client.GetAsync("/api/v1/reports/input-vat-register")).StatusCode);
        Assert.Contains((await tax.Client.GetFromJsonAsync<JsonElement>("/api/v1/reports")).EnumerateArray(), r => r.GetProperty("key").GetString() == "input-vat-register");
    }

    [Fact]
    public async Task A_report_returns_localised_labels_and_an_export_is_a_csv_file_with_a_hash_header()
    {
        var viewer = await SignIn("rep-viewer2@acme.com", "Report Viewer");
        var report = await viewer.Client.GetFromJsonAsync<JsonElement>("/api/v1/reports/payables-aging?locale=ar&calendar=hijri");
        Assert.Equal("أعمار الذمم الدائنة", report.GetProperty("title").GetString());
        Assert.Equal(2, report.GetProperty("tables").GetArrayLength());

        var export = await viewer.Client.GetAsync("/api/v1/reports/payables-aging/export?format=csv&part=summary");
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        Assert.Equal("text/csv", export.Content.Headers.ContentType!.MediaType);
        Assert.Equal(64, export.Headers.GetValues("X-Content-SHA256").Single().Length);
        Assert.Equal("format_invalid", await ErrorCode(await viewer.Client.GetAsync("/api/v1/reports/payables-aging/export?format=docx")));
        Assert.Equal("invalid_period", await ErrorCode(await viewer.Client.GetAsync("/api/v1/reports/exceptions?from=2026-10-10&to=2026-10-01")));
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.Client.GetAsync("/api/v1/reports/not-a-report")).StatusCode);
    }

    [Fact]
    public async Task A_user_limited_to_one_entity_cannot_ask_for_another()
    {
        var mine = await Entity("REP1");
        var other = await Entity("REP2");
        var scoped = await SignIn("rep-scoped@acme.com");
        await _api.CreateRoleAndAssign(_api.TenantA, "rep-scoped@acme.com", "Entity Reports", [Permissions.ReportView, Permissions.EntityView], mine);
        Assert.Equal(HttpStatusCode.OK, (await scoped.Client.GetAsync($"/api/v1/reports/payables-aging?legalEntityId={mine}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await scoped.Client.GetAsync($"/api/v1/reports/payables-aging?legalEntityId={other}")).StatusCode);
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
