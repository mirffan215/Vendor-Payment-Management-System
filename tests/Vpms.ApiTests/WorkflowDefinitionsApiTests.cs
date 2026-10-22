using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Vpms.BuildingBlocks.Security;
using Vpms.Identity.Contracts;
using Vpms.TestSupport;

namespace Vpms.ApiTests;

[Collection(SqlServerCollection.Name)]
public sealed class WorkflowDefinitionsApiTests : IClassFixture<ApiFixtureHolder>
{
    private static readonly Guid Maker = Guid.NewGuid();
    private readonly ApiFixture _api;

    public WorkflowDefinitionsApiTests(ApiFixtureHolder holder) => _api = holder.Api;

    [Fact]
    public async Task Workflow_screens_are_gated_by_permission_for_that_entity()
    {
        var entity = await Entity("WF1");
        var other = await Entity("WF1B");
        var plain = await SignIn("wf-plain1@acme.com");
        Assert.Equal(HttpStatusCode.Forbidden, (await plain.Client.GetAsync($"/api/v1/admin/legal-entities/{entity}/workflow")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await plain.Put($"/api/v1/admin/legal-entities/{entity}/workflow", new { fromMatrix = true, note = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await plain.Client.GetAsync("/api/v1/workflow-changes")).StatusCode);

        var scoped = await SignIn("wf-scoped1@acme.com");
        await _api.CreateRoleAndAssign(_api.TenantA, "wf-scoped1@acme.com", "Entity Workflow Admin", [Permissions.WorkflowConfigure], entity);
        Assert.Equal(HttpStatusCode.OK, (await scoped.Client.GetAsync($"/api/v1/admin/legal-entities/{entity}/workflow")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await scoped.Client.GetAsync($"/api/v1/admin/legal-entities/{other}/workflow")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await scoped.Client.GetAsync($"/api/v1/admin/legal-entities/{other}/workflow/matrix-preview")).StatusCode);
    }

    [Fact]
    public async Task A_workflow_is_proposed_by_one_person_and_activated_by_another_over_http()
    {
        var entity = await Entity("WF2");
        var admin = await SignIn("wf-admin2@acme.com", "Workflow Administrator");
        var approver = await SignIn("wf-chg2@acme.com", "Workflow Change Approver");
        var a1 = await SignIn("wf-a1-2@acme.com", "Invoice Approver");
        var a2 = await SignIn("wf-a2-2@acme.com", "Invoice Approver");

        var spec = new
        {
            name = "Two levels", start = "l1",
            steps = new object[]
            {
                new { id = "l1", type = "Approval", next = "l2", approvers = new[] { new { kind = "User", value = a1.UserId.ToString() } } },
                new { id = "l2", type = "Approval", approvers = new[] { new { kind = "User", value = a2.UserId.ToString() } } },
            },
        };

        var proposed = await admin.Put($"/api/v1/admin/legal-entities/{entity}/workflow", new { spec, fromMatrix = false, note = "Initial" });
        Assert.Equal(HttpStatusCode.OK, proposed.StatusCode);
        var change = await proposed.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Pending", change.GetProperty("status").GetString());
        var id = change.GetProperty("id").GetGuid();

        var state = await admin.Client.GetFromJsonAsync<JsonElement>($"/api/v1/admin/legal-entities/{entity}/workflow");
        Assert.Equal(JsonValueKind.Null, state.GetProperty("active").ValueKind);
        Assert.Equal(id, state.GetProperty("pending").GetProperty("id").GetGuid());

        // The maker has no approval right at all; a second admin who also lacks it is refused too.
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.Client.GetAsync("/api/v1/workflow-changes")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.Post($"/api/v1/workflow-changes/{id}/approve", new { })).StatusCode);

        var queue = await approver.Client.GetFromJsonAsync<JsonElement>("/api/v1/workflow-changes?status=Pending");
        Assert.Contains(queue.EnumerateArray(), c => c.GetProperty("change").GetProperty("id").GetGuid() == id);
        Assert.Equal(HttpStatusCode.OK, (await approver.Post($"/api/v1/workflow-changes/{id}/approve", new { comment = "ok" })).StatusCode);

        var after = await admin.Client.GetFromJsonAsync<JsonElement>($"/api/v1/admin/legal-entities/{entity}/workflow");
        Assert.Equal(1, after.GetProperty("active").GetProperty("versionNo").GetInt32());
        Assert.Equal(2, after.GetProperty("active").GetProperty("spec").GetProperty("steps").GetArrayLength());
    }

    [Fact]
    public async Task Errors_use_stable_codes_and_validation_lists_every_issue()
    {
        var entity = await Entity("WF3");
        var admin = await SignIn("wf-admin3@acme.com", "Workflow Administrator");
        var url = $"/api/v1/admin/legal-entities/{entity}/workflow";

        Assert.Equal("matrix_empty", await ErrorCode(await admin.Put(url, new { fromMatrix = true, note = "from matrix" })));
        Assert.Equal("spec_required", await ErrorCode(await admin.Put(url, new { fromMatrix = false, note = "nothing" })));
        Assert.Equal("reason_required", await ErrorCode(await admin.Put(url, new { fromMatrix = true, note = " " })));

        var loop = new
        {
            name = "Loop", start = "a",
            steps = new object[]
            {
                new { id = "a", type = "Approval", next = "b", approvers = new[] { new { kind = "Role", value = "Invoice Approver" } } },
                new { id = "b", type = "Approval", next = "a", approvers = new[] { new { kind = "Role", value = "Invoice Approver" } } },
            },
        };
        var issues = await (await admin.Post($"{url}/validate", loop)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains(issues.EnumerateArray(), i => i.GetProperty("code").GetString() == "cycle");
        Assert.Equal("invalid_definition", await ErrorCode(await admin.Put(url, new { spec = loop, fromMatrix = false, note = "bad" })));
    }

    [Fact]
    public async Task Simulation_shows_the_route_an_invoice_would_take()
    {
        var entity = await Entity("WF4");
        var admin = await SignIn("wf-admin4@acme.com", "Workflow Administrator");
        var a1 = await SignIn("wf-a1-4@acme.com", "Invoice Approver");

        var spec = new
        {
            name = "Amount gate", start = "gate",
            steps = new object[]
            {
                new
                {
                    id = "gate", type = "Condition", @default = "$end",
                    cases = new[] { new { all = new[] { new { field = "amount", op = "gt", values = new[] { "1000" } } }, next = "review" } },
                },
                new { id = "review", type = "Approval", approvers = new[] { new { kind = "User", value = a1.UserId.ToString() } } },
            },
        };

        var big = await (await admin.Post($"/api/v1/admin/legal-entities/{entity}/workflow/simulate",
            new { spec, amount = 5000, currency = "SAR" })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Approved", big.GetProperty("outcome").GetString());
        Assert.Equal("review", big.GetProperty("route")[0].GetProperty("stepId").GetString());

        var small = await (await admin.Post($"/api/v1/admin/legal-entities/{entity}/workflow/simulate",
            new { spec, amount = 10, currency = "SAR" })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, small.GetProperty("route").GetArrayLength());

        var noWorkflow = await (await admin.Post($"/api/v1/admin/legal-entities/{entity}/workflow/simulate",
            new { amount = 10, currency = "SAR" })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("NoWorkflow", noWorkflow.GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task One_person_cannot_hold_both_configuring_workflows_and_approving_them_or_invoices()
    {
        await SignIn("wf-both@acme.com", "Workflow Administrator");
        await Assert.ThrowsAsync<SegregationOfDutiesException>(() => _api.AssignRoleAsync(_api.TenantA, "wf-both@acme.com", "Workflow Change Approver", null));
        await Assert.ThrowsAsync<SegregationOfDutiesException>(() => _api.AssignRoleAsync(_api.TenantA, "wf-both@acme.com", "Invoice Approver", null));
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
