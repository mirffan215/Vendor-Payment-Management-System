using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Vpms.BuildingBlocks.Security;
using Vpms.TestSupport;

namespace Vpms.ApiTests;

[Collection(SqlServerCollection.Name)]
public sealed class ErpApiTests : IClassFixture<ApiFixtureHolder>
{
    private static readonly Guid Maker = Guid.NewGuid();
    private readonly ApiFixture _api;

    public ErpApiTests(ApiFixtureHolder holder) => _api = holder.Api;

    private static object Connection(string secretRef = "erp-client-secret") => new
    {
        provider = "D365Finance", baseUrl = "https://contoso.operations.dynamics.com", authorityTenantId = "tenant-1", clientId = "client-1", secretRef, entitySets = new { },
        pageSize = 200, timeoutSeconds = 60,
    };

    private static object Mapping(string company = "USMF") => new { company, journalName = "VENDINV", postingProfile = (string?)null, paymentTermsMap = new { }, taxCodeMap = new { }, dimensions = Array.Empty<object>() };

    [Fact]
    public async Task ERP_screens_are_gated_by_permission()
    {
        var entity = await Entity("ERP1");
        var plain = await SignIn("erp-plain@acme.com");
        foreach (var url in new[]
                 {
                     "/api/v1/admin/erp/connection", $"/api/v1/admin/legal-entities/{entity}/erp-mapping", "/api/v1/erp-changes", "/api/v1/erp/sync/summary", "/api/v1/erp/sync",
                     "/api/v1/erp/reconciliation/summary", "/api/v1/erp/reconciliation/items", "/api/v1/erp/reconciliation/export",
                 })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await plain.Client.GetAsync(url)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.Forbidden, (await plain.Put("/api/v1/admin/erp/connection", new { config = Connection(), note = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await plain.Post("/api/v1/erp/reconciliation/run", new { legalEntityId = entity })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await plain.Post($"/api/v1/admin/legal-entities/{entity}/erp/test", new { })).StatusCode);
    }

    [Fact]
    public async Task A_connection_is_proposed_by_one_person_and_activated_by_another_and_never_returns_a_secret()
    {
        var admin = await SignIn("erp-admin2@acme.com", "ERP Administrator");
        var approver = await SignIn("erp-chg2@acme.com", "ERP Change Approver");

        var proposed = await admin.Put("/api/v1/admin/erp/connection", new { config = Connection(), note = "Initial connection" });
        Assert.Equal(HttpStatusCode.OK, proposed.StatusCode);
        var body = await proposed.Content.ReadAsStringAsync();
        Assert.DoesNotContain("clientSecret", body, StringComparison.OrdinalIgnoreCase);
        var id = JsonDocument.Parse(body).RootElement.GetProperty("id").GetGuid();
        Assert.Equal("erp-client-secret", JsonDocument.Parse(body).RootElement.GetProperty("connection").GetProperty("secretRef").GetString());

        var state = await admin.Client.GetFromJsonAsync<JsonElement>("/api/v1/admin/erp/connection");
        Assert.Equal(JsonValueKind.Null, state.GetProperty("active").ValueKind);

        // the maker cannot decide; a person without the approval right is told nothing exists
        Assert.Equal(HttpStatusCode.NotFound, (await admin.Post($"/api/v1/erp-changes/{id}/approve", new { })).StatusCode);
        var queue = await approver.Client.GetFromJsonAsync<JsonElement>("/api/v1/erp-changes?status=Pending");
        Assert.Contains(queue.EnumerateArray(), c => c.GetProperty("change").GetProperty("id").GetGuid() == id);
        Assert.Equal(HttpStatusCode.OK, (await approver.Post($"/api/v1/erp-changes/{id}/approve", new { comment = "ok" })).StatusCode);

        var after = await admin.Client.GetFromJsonAsync<JsonElement>("/api/v1/admin/erp/connection");
        Assert.Equal(1, after.GetProperty("active").GetProperty("versionNo").GetInt32());
    }

    [Fact]
    public async Task Errors_use_stable_codes()
    {
        var entity = await Entity("ERP3");
        var admin = await SignIn("erp-admin3@acme.com", "ERP Administrator");
        Assert.Equal("secret_ref_invalid", await ErrorCode(await admin.Put("/api/v1/admin/erp/connection", new { config = Connection("not a vault name!"), note = "bad" })));
        Assert.Equal("reason_required", await ErrorCode(await admin.Put("/api/v1/admin/erp/connection", new { config = Connection(), note = " " })));
        Assert.Equal("company_invalid", await ErrorCode(await admin.Put($"/api/v1/admin/legal-entities/{entity}/erp-mapping", new { config = Mapping(""), note = "bad" })));
    }

    [Fact]
    public async Task Testing_a_connection_whose_secret_is_not_in_the_vault_explains_why_without_calling_the_erp()
    {
        var entity = await Entity("ERP4");
        var admin = await SignIn("erp-admin4@acme.com", "ERP Administrator");
        var approver = await SignIn("erp-chg4@acme.com", "ERP Change Approver");
        var c = await (await admin.Put("/api/v1/admin/erp/connection", new { config = Connection("absent-secret"), note = "connect" })).Content.ReadFromJsonAsync<JsonElement>();
        await approver.Post($"/api/v1/erp-changes/{c.GetProperty("id").GetGuid()}/approve", new { });
        var m = await (await admin.Put($"/api/v1/admin/legal-entities/{entity}/erp-mapping", new { config = Mapping(), note = "map" })).Content.ReadFromJsonAsync<JsonElement>();
        await approver.Post($"/api/v1/erp-changes/{m.GetProperty("id").GetGuid()}/approve", new { });

        var result = await (await admin.Post($"/api/v1/admin/legal-entities/{entity}/erp/test", new { })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(result.GetProperty("ok").GetBoolean());
        Assert.Equal("secret_unavailable", result.GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task The_operator_sees_the_queue_and_reconciliation_report_but_cannot_change_configuration()
    {
        var entity = await Entity("ERP5");
        var op = await SignIn("erp-op5@acme.com", "ERP Operator");

        var summary = await op.Client.GetFromJsonAsync<JsonElement>("/api/v1/erp/sync/summary");
        Assert.Equal(0, summary.GetProperty("deadLetter").GetInt32());
        Assert.Equal(HttpStatusCode.OK, (await op.Client.GetAsync("/api/v1/erp/sync?status=DeadLetter")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await op.Post($"/api/v1/erp/sync/{Guid.NewGuid()}/retry", new { reason = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await op.Put("/api/v1/admin/erp/connection", new { config = Connection(), note = "x" })).StatusCode);

        var run = await (await op.Post("/api/v1/erp/reconciliation/run", new { legalEntityId = entity })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Failed", run.GetProperty("status").GetString());
        Assert.Equal("connection_missing", run.GetProperty("errorCode").GetString());

        var export = await op.Client.GetAsync("/api/v1/erp/reconciliation/export");
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        Assert.Equal("text/csv", export.Content.Headers.ContentType!.MediaType);
        Assert.StartsWith("Type,Status,", await export.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task One_person_cannot_hold_both_configuring_the_erp_and_approving_the_change()
    {
        await SignIn("erp-both@acme.com", "ERP Administrator");
        await Assert.ThrowsAsync<SegregationOfDutiesException>(() => _api.AssignRoleAsync(_api.TenantA, "erp-both@acme.com", "ERP Change Approver", null));
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
