using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Vpms.BuildingBlocks.Security;
using Vpms.Identity.Contracts;
using Vpms.TestSupport;

namespace Vpms.ApiTests;

[Collection(SqlServerCollection.Name)]
public sealed class VerificationRulesApiTests : IClassFixture<ApiFixtureHolder>
{
    private static readonly Guid Maker = Guid.NewGuid();
    private readonly ApiFixture _api;

    public VerificationRulesApiTests(ApiFixtureHolder holder) => _api = holder.Api;

    [Fact]
    public async Task The_catalogue_describes_every_configurable_rule()
    {
        var admin = await SignIn("vr-admin0@acme.com", "Verification Administrator");
        var catalogue = await admin.Client.GetFromJsonAsync<JsonElement>("/api/v1/verification-rules/catalog");
        var codes = catalogue.EnumerateArray().Select(r => r.GetProperty("code").GetString()).ToList();
        Assert.Contains("duplicate.exact", codes);
        Assert.Contains("po.match", codes);
        var vendorState = catalogue.EnumerateArray().Single(r => r.GetProperty("code").GetString() == "vendor.state");
        Assert.False(vendorState.GetProperty("canDisable").GetBoolean());
    }

    [Fact]
    public async Task Settings_are_gated_by_permission_for_that_entity()
    {
        var entity = await Entity("VR1");
        var other = await Entity("VR1B");
        var plain = await SignIn("vr-plain1@acme.com");
        Assert.Equal(HttpStatusCode.Forbidden, (await plain.Client.GetAsync($"/api/v1/admin/legal-entities/{entity}/verification-rules")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await plain.Put($"/api/v1/admin/legal-entities/{entity}/verification-rules", Body("po.required", true, "Flag"))).StatusCode);

        var onlyOne = await SignIn("vr-one1@acme.com");
        await _api.CreateRoleAndAssign(_api.TenantA, "vr-one1@acme.com", "Entity Rules Admin", [Permissions.VerificationConfigure], entity);
        Assert.Equal(HttpStatusCode.OK, (await onlyOne.Client.GetAsync($"/api/v1/admin/legal-entities/{entity}/verification-rules")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await onlyOne.Client.GetAsync($"/api/v1/admin/legal-entities/{other}/verification-rules")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await onlyOne.Put($"/api/v1/admin/legal-entities/{other}/verification-rules", Body("po.required", true, "Flag"))).StatusCode);
    }

    [Fact]
    public async Task Tightening_is_immediate_and_loosening_needs_a_second_person_over_http()
    {
        var entity = await Entity("VR2");
        var admin = await SignIn("vr-admin2@acme.com", "Verification Administrator");
        var approver = await SignIn("vr-approver2@acme.com", "Verification Approver");

        var tighten = await admin.Put($"/api/v1/admin/legal-entities/{entity}/verification-rules", Body("po.required", true, "Block", "PO is mandatory"));
        Assert.Equal(HttpStatusCode.OK, tighten.StatusCode);
        Assert.Equal("Active", (await tighten.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());

        var loosen = await admin.Put($"/api/v1/admin/legal-entities/{entity}/verification-rules", Body("duplicate.exact", true, "Flag", "Resends are common"));
        var pending = await loosen.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Pending", pending.GetProperty("status").GetString());
        var changeId = pending.GetProperty("id").GetGuid();

        var current = await admin.Client.GetFromJsonAsync<JsonElement>($"/api/v1/admin/legal-entities/{entity}/verification-rules");
        Assert.Equal("Block", current.GetProperty("active").GetProperty("config").GetProperty("rules").GetProperty("duplicate.exact").GetProperty("severity").GetString());
        Assert.Equal(changeId, current.GetProperty("pending").GetProperty("id").GetGuid());

        // the requester has no approval right at all; the approver sees and decides it
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.Client.GetAsync("/api/v1/verification-rule-changes")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.Post($"/api/v1/verification-rule-changes/{changeId}/approve", new { })).StatusCode);

        var queue = await approver.Client.GetFromJsonAsync<JsonElement>("/api/v1/verification-rule-changes?status=Pending");
        Assert.Contains(queue.EnumerateArray(), c => c.GetProperty("change").GetProperty("id").GetGuid() == changeId);
        var approved = await approver.Post($"/api/v1/verification-rule-changes/{changeId}/approve", new { comment = "Agreed" });
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);

        var after = await admin.Client.GetFromJsonAsync<JsonElement>($"/api/v1/admin/legal-entities/{entity}/verification-rules");
        Assert.Equal("Flag", after.GetProperty("active").GetProperty("config").GetProperty("rules").GetProperty("duplicate.exact").GetProperty("severity").GetString());
        Assert.Equal(JsonValueKind.Null, after.GetProperty("pending").ValueKind);

        var history = await admin.Client.GetFromJsonAsync<JsonElement>($"/api/v1/admin/legal-entities/{entity}/verification-rules/history");
        Assert.Equal(2, history.GetArrayLength());
    }

    [Fact]
    public async Task Errors_use_stable_codes()
    {
        var entity = await Entity("VR3");
        var admin = await SignIn("vr-admin3@acme.com", "Verification Administrator");
        Assert.Equal("unknown_rule", await ErrorCode(await admin.Put($"/api/v1/admin/legal-entities/{entity}/verification-rules", Body("not.a.rule", true, "Flag"))));
        Assert.Equal("rule_not_disableable", await ErrorCode(await admin.Put($"/api/v1/admin/legal-entities/{entity}/verification-rules", Body("vendor.state", false, "Block"))));
        Assert.Equal("reason_required", await ErrorCode(await admin.Put($"/api/v1/admin/legal-entities/{entity}/verification-rules", Body("po.required", true, "Flag", " "))));
        var url = $"/api/v1/admin/legal-entities/{entity}/verification-rules";
        Assert.Equal(HttpStatusCode.OK, (await admin.Put(url, Body("po.required", true, "Flag"))).StatusCode);
        var again = await admin.Put(url, Body("po.required", true, "Flag"));
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("no_change", await ErrorCode(again));
    }

    [Fact]
    public async Task One_person_cannot_hold_both_configuring_and_approving()
    {
        await SignIn("vr-both@acme.com", "Verification Administrator");
        await Assert.ThrowsAsync<SegregationOfDutiesException>(() => _api.AssignRoleAsync(_api.TenantA, "vr-both@acme.com", "Verification Approver", null));
    }

    // --- helpers -------------------------------------------------------------------------------------------

    private Task<Guid> Entity(string code) => ActiveVendorKit.CreateEntityAsync(_api.Services, _api.TenantA, Maker, code);

    private static object Body(string rule, bool enabled, string severity, string note = "Change") =>
        new { note, rules = new Dictionary<string, object> { [rule] = new { enabled, severity, parameters = new Dictionary<string, decimal>() } } };

    private async Task<Session> SignIn(string email, string? role = null)
    {
        var client = _api.NewBrowser();
        _api.IdpA.NextUser = new FakeUser("sub-" + email, email, email.Split('@')[0]);
        await AuthenticationTests.SignInAsync(client, _api.IdpA, _api.ProviderA, "/");
        if (role is not null) await _api.AssignRoleAsync(_api.TenantA, email, role, null);
        var csrf = (await client.GetFromJsonAsync<JsonElement>("/api/v1/session")).GetProperty("csrfToken").GetString()!;
        return new Session(client, csrf);
    }

    private static async Task<string?> ErrorCode(HttpResponseMessage r) => (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errorCode").GetString();

    private sealed record Session(HttpClient Client, string Csrf)
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
