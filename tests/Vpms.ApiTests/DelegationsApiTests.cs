using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Vpms.TestSupport;

namespace Vpms.ApiTests;

[Collection(SqlServerCollection.Name)]
public sealed class DelegationsApiTests : IClassFixture<ApiFixtureHolder>
{
    private static readonly Guid Maker = Guid.NewGuid();
    private readonly ApiFixture _api;

    public DelegationsApiTests(ApiFixtureHolder holder) => _api = holder.Api;

    [Fact]
    public async Task An_approver_delegates_lists_and_revokes_over_http()
    {
        var entity = await ActiveVendorKit.CreateEntityAsync(_api.Services, _api.TenantA, Maker, "DL1");
        var a = await SignIn("dl1-a@acme.com", "Invoice Approver");
        var b = await SignIn("dl1-b@acme.com", "Invoice Approver");
        var from = DateTimeOffset.UtcNow;

        var created = await a.Post("/api/v1/delegations", new { legalEntityId = entity, delegateId = b.UserId, validFrom = from, validTo = from.AddDays(5), reason = "Leave" });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var mineA = await a.Client.GetFromJsonAsync<JsonElement>("/api/v1/delegations?activeOnly=true");
        var mineB = await b.Client.GetFromJsonAsync<JsonElement>("/api/v1/delegations?activeOnly=true");
        Assert.Equal(id, mineA[0].GetProperty("id").GetGuid());
        Assert.Equal(id, mineB[0].GetProperty("id").GetGuid());

        var overlap = await a.Post("/api/v1/delegations", new { legalEntityId = entity, delegateId = b.UserId, validFrom = from.AddDays(1), validTo = from.AddDays(2), reason = "Again" });
        Assert.Equal(HttpStatusCode.Conflict, overlap.StatusCode);
        Assert.Equal("delegation_overlaps", await ErrorCode(overlap));

        var stranger = await SignIn("dl1-c@acme.com", "Invoice Approver");
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.Post($"/api/v1/delegations/{id}/revoke")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await b.Post($"/api/v1/delegations/{id}/revoke")).StatusCode);
        Assert.Equal(0, (await a.Client.GetFromJsonAsync<JsonElement>("/api/v1/delegations?activeOnly=true")).GetArrayLength());
    }

    [Fact]
    public async Task Delegation_rules_and_permissions_are_enforced()
    {
        var entity = await ActiveVendorKit.CreateEntityAsync(_api.Services, _api.TenantA, Maker, "DL2");
        var a = await SignIn("dl2-a@acme.com", "Invoice Approver");
        var b = await SignIn("dl2-b@acme.com", "Invoice Approver");
        var clerk = await SignIn("dl2-clerk@acme.com", "Invoice Clerk");
        var from = DateTimeOffset.UtcNow;

        Assert.Equal(HttpStatusCode.Forbidden, (await clerk.Post("/api/v1/delegations", new { legalEntityId = entity, delegateId = b.UserId, validFrom = from, validTo = from.AddDays(1), reason = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await clerk.Client.GetAsync("/api/v1/delegations")).StatusCode);

        var self = await a.Post("/api/v1/delegations", new { legalEntityId = entity, delegateId = a.UserId, validFrom = from, validTo = from.AddDays(1), reason = "x" });
        Assert.Equal("self_delegation", await ErrorCode(self));
        var notApprover = await a.Post("/api/v1/delegations", new { legalEntityId = entity, delegateId = clerk.UserId, validFrom = from, validTo = from.AddDays(1), reason = "x" });
        Assert.Equal("approver_not_permitted", await ErrorCode(notApprover));
        var onBehalf = await a.Post("/api/v1/delegations", new { legalEntityId = entity, delegatorId = b.UserId, delegateId = a.UserId, validFrom = from, validTo = from.AddDays(1), reason = "x" });
        Assert.Equal(HttpStatusCode.Forbidden, onBehalf.StatusCode);
        Assert.Equal("not_allowed", await ErrorCode(onBehalf));

        var admin = await SignIn("dl2-admin@acme.com", "Approval Coordinator");
        var byAdmin = await admin.Post("/api/v1/delegations", new { legalEntityId = entity, delegatorId = b.UserId, delegateId = a.UserId, validFrom = from, validTo = from.AddDays(1), reason = "b is on leave" });
        Assert.Equal(HttpStatusCode.OK, byAdmin.StatusCode);
        Assert.Equal(1, (await admin.Client.GetFromJsonAsync<JsonElement>($"/api/v1/admin/legal-entities/{entity}/delegations")).GetArrayLength());
        Assert.Equal(HttpStatusCode.Forbidden, (await a.Client.GetAsync($"/api/v1/admin/legal-entities/{entity}/delegations")).StatusCode);
    }

    [Fact]
    public async Task Notices_are_listed_for_the_recipient_only()
    {
        var a = await SignIn("dl3-a@acme.com", "Invoice Approver");
        var clerk = await SignIn("dl3-clerk@acme.com", "Invoice Clerk");
        Assert.Equal(0, (await a.Client.GetFromJsonAsync<JsonElement>("/api/v1/approvals/notices")).GetArrayLength());
        Assert.Equal(HttpStatusCode.Forbidden, (await clerk.Client.GetAsync("/api/v1/approvals/notices")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await a.Post($"/api/v1/approvals/notices/{Guid.NewGuid()}/read")).StatusCode);
    }

    private async Task<Session> SignIn(string email, string role)
    {
        var client = _api.NewBrowser();
        _api.IdpA.NextUser = new FakeUser("sub-" + email, email, email.Split('@')[0]);
        await AuthenticationTests.SignInAsync(client, _api.IdpA, _api.ProviderA, "/");
        await _api.AssignRoleAsync(_api.TenantA, email, role, null);
        var me = await client.GetFromJsonAsync<JsonElement>("/api/v1/session");
        return new Session(client, me.GetProperty("csrfToken").GetString()!, Guid.Parse(me.GetProperty("userId").GetString()!));
    }

    private static async Task<string?> ErrorCode(HttpResponseMessage r) => (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errorCode").GetString();

    private sealed record Session(HttpClient Client, string Csrf, Guid UserId)
    {
        public Task<HttpResponseMessage> Post(string url, object? body = null)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body ?? new { }) };
            request.Headers.Add("X-XSRF-TOKEN", Csrf);
            return Client.SendAsync(request);
        }
    }
}
