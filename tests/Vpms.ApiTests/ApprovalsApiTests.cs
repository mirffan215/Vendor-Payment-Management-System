using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Vpms.BuildingBlocks.Security;
using Vpms.CustomerAdmin.Application;
using Vpms.Identity.Contracts;
using Vpms.TestSupport;

namespace Vpms.ApiTests;

[Collection(SqlServerCollection.Name)]
public sealed class ApprovalsApiTests : IClassFixture<ApiFixtureHolder>
{
    private static readonly Guid Maker = Guid.NewGuid();
    private static readonly Guid Checker = Guid.NewGuid();
    private readonly ApiFixture _api;

    public ApprovalsApiTests(ApiFixtureHolder holder) => _api = holder.Api;

    [Fact]
    public async Task An_invoice_is_approved_through_two_levels_over_http()
    {
        var (entity, vendor) = await Master("AP1");
        var w = await Workflow(entity, "ap1");
        var clerk = await SignIn("ap1-clerk@acme.com", "Invoice Clerk");
        var invoiceId = await VerifiedInvoice(clerk, entity, vendor, "AP-1");

        var sent = await clerk.Post($"/api/v1/invoices/{invoiceId}/send-for-approval");
        Assert.Equal(HttpStatusCode.OK, sent.StatusCode);
        Assert.Equal("InApproval", (await sent.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());

        var inbox = await w.A1.Client.GetFromJsonAsync<JsonElement>("/api/v1/approvals/inbox");
        var item = inbox.EnumerateArray().Single(i => i.GetProperty("subjectId").GetGuid() == invoiceId);
        Assert.Equal("l1", item.GetProperty("stepId").GetString());
        Assert.Contains("AP-1", item.GetProperty("subjectLabel").GetString());
        var task1 = item.GetProperty("taskId").GetGuid();

        var detail = await w.A1.Client.GetFromJsonAsync<JsonElement>($"/api/v1/approvals/tasks/{task1}");
        Assert.Equal("Active", detail.GetProperty("status").GetString());
        Assert.Equal(1, detail.GetProperty("definitionVersion").GetInt32());

        var first = await w.A1.Post($"/api/v1/approvals/tasks/{task1}/decision", new { decision = "Approve", comment = "ok" });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var task2 = (await w.A2.Client.GetFromJsonAsync<JsonElement>("/api/v1/approvals/inbox")).EnumerateArray().Single(i => i.GetProperty("subjectId").GetGuid() == invoiceId).GetProperty("taskId").GetGuid();
        var second = await w.A2.Post($"/api/v1/approvals/tasks/{task2}/decision", new { decision = "Approve" });
        Assert.Equal("Approved", (await second.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());

        var final = await clerk.Client.GetFromJsonAsync<JsonElement>($"/api/v1/invoices/{invoiceId}");
        Assert.Equal("Approved", final.GetProperty("status").GetString());
        Assert.Equal(1, final.GetProperty("approvedVersion").GetInt32());

        var history = await clerk.Client.GetFromJsonAsync<JsonElement>($"/api/v1/invoices/{invoiceId}/approvals");
        var run = history.EnumerateArray().Single();
        Assert.Equal("Approved", run.GetProperty("status").GetString());
        Assert.Equal(2, run.GetProperty("tasks").GetArrayLength());
        Assert.Equal("ok", run.GetProperty("tasks")[0].GetProperty("decisions")[0].GetProperty("comment").GetString());
    }

    [Fact]
    public async Task Approval_endpoints_are_gated_by_permission_and_legal_entity()
    {
        var (entity, vendor) = await Master("AP2");
        var w = await Workflow(entity, "ap2");
        var clerk = await SignIn("ap2-clerk@acme.com", "Invoice Clerk");
        var invoiceId = await VerifiedInvoice(clerk, entity, vendor, "AP-2");
        await clerk.Post($"/api/v1/invoices/{invoiceId}/send-for-approval");
        var task = (await w.A1.Client.GetFromJsonAsync<JsonElement>("/api/v1/approvals/inbox")).EnumerateArray().Single(i => i.GetProperty("subjectId").GetGuid() == invoiceId).GetProperty("taskId").GetGuid();

        // a clerk has no approval right at all
        Assert.Equal(HttpStatusCode.Forbidden, (await clerk.Client.GetAsync("/api/v1/approvals/inbox")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await clerk.Client.GetAsync($"/api/v1/approvals/tasks/{task}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await clerk.Post($"/api/v1/approvals/tasks/{task}/decision", new { decision = "Approve" })).StatusCode);

        // an approver for another legal entity only cannot see or decide this entity's task
        var other = await SignIn("ap2-other@acme.com");
        await _api.CreateRoleAndAssign(_api.TenantA, "ap2-other@acme.com", "Other Entity Approver", [Permissions.InvoiceApprove, Permissions.InvoiceView], await ActiveVendorKit.CreateEntityAsync(_api.Services, _api.TenantA, Maker, "AP2X"));
        Assert.Equal(HttpStatusCode.NotFound, (await other.Client.GetAsync($"/api/v1/approvals/tasks/{task}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.Post($"/api/v1/approvals/tasks/{task}/decision", new { decision = "Approve" })).StatusCode);
        Assert.Equal(0, (await other.Client.GetFromJsonAsync<JsonElement>("/api/v1/approvals/inbox")).GetArrayLength());
        Assert.Equal(HttpStatusCode.NotFound, (await other.Client.GetAsync($"/api/v1/invoices/{invoiceId}/approvals")).StatusCode);

        // sending for approval needs the edit permission
        var viewer = await SignIn("ap2-viewer@acme.com", "Invoice Approver");
        Assert.Equal(HttpStatusCode.NotFound, (await viewer.Post($"/api/v1/invoices/{invoiceId}/send-for-approval")).StatusCode);
    }

    [Fact]
    public async Task Errors_use_stable_codes()
    {
        var (entity, vendor) = await Master("AP3");
        var clerk = await SignIn("ap3-clerk@acme.com", "Invoice Clerk");
        var noWorkflow = await VerifiedInvoice(clerk, entity, vendor, "AP-3A");
        var denied = await clerk.Post($"/api/v1/invoices/{noWorkflow}/send-for-approval");
        Assert.Equal(HttpStatusCode.Conflict, denied.StatusCode);
        Assert.Equal("no_workflow", await ErrorCode(denied));

        var w = await Workflow(entity, "ap3");
        var invoiceId = await VerifiedInvoice(clerk, entity, vendor, "AP-3B");
        await clerk.Post($"/api/v1/invoices/{invoiceId}/send-for-approval");
        var task = (await w.A1.Client.GetFromJsonAsync<JsonElement>("/api/v1/approvals/inbox")).EnumerateArray().Single(i => i.GetProperty("subjectId").GetGuid() == invoiceId).GetProperty("taskId").GetGuid();

        var noComment = await w.A1.Post($"/api/v1/approvals/tasks/{task}/decision", new { decision = "Reject" });
        Assert.Equal(HttpStatusCode.BadRequest, noComment.StatusCode);
        Assert.Equal("comment_required", await ErrorCode(noComment));

        var outsider = await w.A2.Post($"/api/v1/approvals/tasks/{task}/decision", new { decision = "Approve" });
        Assert.Equal(HttpStatusCode.Forbidden, outsider.StatusCode);
        Assert.Equal("not_an_approver", await ErrorCode(outsider));

        var twice = await clerk.Post($"/api/v1/invoices/{invoiceId}/send-for-approval");
        Assert.Equal("invalid_transition", await ErrorCode(twice));

        Assert.Equal("decision_invalid", await ErrorCode(await w.A1.Post($"/api/v1/approvals/tasks/{task}/decision", new { decision = "Maybe" })));
    }

    [Fact]
    public async Task Rejecting_an_invoice_as_an_approver_rejects_it_for_the_vendor_record_too()
    {
        var (entity, vendor) = await Master("AP4");
        var w = await Workflow(entity, "ap4");
        var clerk = await SignIn("ap4-clerk@acme.com", "Invoice Clerk");
        var invoiceId = await VerifiedInvoice(clerk, entity, vendor, "AP-4");
        await clerk.Post($"/api/v1/invoices/{invoiceId}/send-for-approval");
        var task = (await w.A1.Client.GetFromJsonAsync<JsonElement>("/api/v1/approvals/inbox")).EnumerateArray().Single(i => i.GetProperty("subjectId").GetGuid() == invoiceId).GetProperty("taskId").GetGuid();

        var rejected = await w.A1.Post($"/api/v1/approvals/tasks/{task}/decision", new { decision = "Reject", comment = "Duplicate of a paid invoice" });
        Assert.Equal(HttpStatusCode.OK, rejected.StatusCode);
        var invoice = await clerk.Client.GetFromJsonAsync<JsonElement>($"/api/v1/invoices/{invoiceId}");
        Assert.Equal("Rejected", invoice.GetProperty("status").GetString());
        Assert.Equal("Duplicate of a paid invoice", invoice.GetProperty("statusReason").GetString());
    }

    [Fact]
    public async Task Editing_an_invoice_in_approval_restarts_approval_by_default_and_reports_what_changed()
    {
        var (entity, vendor) = await Master("AP6");
        var w = await Workflow(entity, "ap6");
        var clerk = await SignIn("ap6-clerk@acme.com", "Invoice Clerk");
        var invoiceId = await VerifiedInvoice(clerk, entity, vendor, "AP-6");
        await clerk.Post($"/api/v1/invoices/{invoiceId}/send-for-approval");

        var current = await clerk.Client.GetFromJsonAsync<JsonElement>($"/api/v1/invoices/{invoiceId}");
        Assert.Equal(HttpStatusCode.BadRequest, (await clerk.Post($"/api/v1/invoices/{invoiceId}/revise", new { data = Revised(current, 200m), reason = " " })).StatusCode);

        var revised = await clerk.Post($"/api/v1/invoices/{invoiceId}/revise", new { data = Revised(current, 200m), reason = "Supplier corrected the amount" });
        Assert.Equal(HttpStatusCode.OK, revised.StatusCode);
        var body = await revised.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("material").GetBoolean());
        Assert.Contains("total", body.GetProperty("changedFields").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal("Verified", body.GetProperty("invoice").GetProperty("status").GetString());
        Assert.Equal(0, (await w.A1.Client.GetFromJsonAsync<JsonElement>("/api/v1/approvals/inbox")).EnumerateArray().Count(i => i.GetProperty("subjectId").GetGuid() == invoiceId));

        // only people who can edit invoices may revise
        Assert.Equal(HttpStatusCode.NotFound, (await w.A1.Post($"/api/v1/invoices/{invoiceId}/revise", new { data = Revised(current, 300m), reason = "x" })).StatusCode);
    }

    [Fact]
    public async Task Materiality_settings_need_a_second_person_over_http()
    {
        var (entity, _) = await Master("AP7");
        var admin = await SignIn("ap7-admin@acme.com", "Workflow Administrator");
        var changer = await SignIn("ap7-changer@acme.com", "Workflow Change Approver");
        var url = $"/api/v1/admin/legal-entities/{entity}/approval-materiality";

        var initial = await admin.Client.GetFromJsonAsync<JsonElement>(url);
        Assert.Equal("Default", initial.GetProperty("active").GetProperty("status").GetString());
        Assert.Equal(0m, initial.GetProperty("active").GetProperty("config").GetProperty("amountPercent").GetDecimal());

        var bad = await admin.Put(url, new { config = new { amountPercent = 500m, amountAbsolute = 0m, materialFields = new[] { "total" } }, note = "x" });
        Assert.Equal("percent_invalid", await ErrorCode(bad));

        var proposed = await admin.Put(url, new { config = new { amountPercent = 2m, amountAbsolute = 10m, materialFields = new[] { "total", "currency" } }, note = "Tolerate rounding" });
        Assert.Equal(HttpStatusCode.OK, proposed.StatusCode);
        var id = (await proposed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NotFound, (await admin.Post($"/api/v1/approval-materiality-changes/{id}/approve", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await changer.Post($"/api/v1/approval-materiality-changes/{id}/approve", new { comment = "ok" })).StatusCode);

        var after = await admin.Client.GetFromJsonAsync<JsonElement>(url);
        Assert.Equal(2m, after.GetProperty("active").GetProperty("config").GetProperty("amountPercent").GetDecimal());
        var fields = await admin.Client.GetFromJsonAsync<JsonElement>("/api/v1/approval-materiality/fields");
        Assert.Contains("total", fields.GetProperty("money").EnumerateArray().Select(e => e.GetString()));
    }

    private static object Revised(JsonElement invoice, decimal net)
    {
        var tax = Math.Round(net * 0.15m, 2);
        return new
        {
            invoiceNumber = invoice.GetProperty("invoiceNumber").GetString(), invoiceDate = invoice.GetProperty("invoiceDate").GetString(), dueDate = (string?)null, currencyCode = "SAR",
            subtotal = net, taxTotal = tax, total = net + tax, poReference = (string?)null, supplierTaxInvoiceRef = (string?)null,
            lines = new[] { new { description = "Consulting", quantity = 1m, unitPrice = net, lineAmount = net, taxCode = "S", taxRate = 0.15m, taxAmount = tax, poLineRef = (string?)null, dimensionsJson = (string?)null } },
        };
    }

    [Fact]
    public async Task One_person_cannot_hold_both_invoice_preparation_and_approval()
    {
        await SignIn("ap5-both@acme.com", "Invoice Clerk");
        await Assert.ThrowsAsync<SegregationOfDutiesException>(() => _api.AssignRoleAsync(_api.TenantA, "ap5-both@acme.com", "Invoice Approver", null));
    }

    // --- helpers -------------------------------------------------------------------------------------------

    private sealed record Approvers(Session Admin, Session A1, Session A2);

    /// <summary>A two-level workflow (a1 then a2) published through the API by two different administrators.</summary>
    private async Task<Approvers> Workflow(Guid entity, string prefix)
    {
        var admin = await SignIn($"{prefix}-wfadmin@acme.com", "Workflow Administrator");
        var changer = await SignIn($"{prefix}-wfchange@acme.com", "Workflow Change Approver");
        var a1 = await SignIn($"{prefix}-a1@acme.com", "Invoice Approver");
        var a2 = await SignIn($"{prefix}-a2@acme.com", "Invoice Approver");
        foreach (var a in new[] { a1, a2 })
        {
            await ActiveVendorKit.AsAsync(_api.Services, _api.TenantA, Maker, async s =>
            {
                await s.GetRequiredService<IApprovalAdminService>().CreateAuthorityAsync(
                    new CreateApprovalAuthority(entity, a.UserId, 1_000_000m, "SAR", null, DateTimeOffset.UtcNow.AddDays(-1), null));
                return 0;
            });
        }

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
        var id = (await proposed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await changer.Post($"/api/v1/workflow-changes/{id}/approve", new { })).StatusCode);
        return new Approvers(admin, a1, a2);
    }

    private async Task<(Guid Entity, Guid Vendor)> Master(string code)
    {
        var entity = await ActiveVendorKit.CreateEntityAsync(_api.Services, _api.TenantA, Maker, code);
        var vendor = await ActiveVendorKit.ActiveVendorAsync(_api.Services, _api.TenantA, entity, "Vendor " + code, Maker, Checker);
        return (entity, vendor);
    }

    private static async Task<Guid> VerifiedInvoice(Session clerk, Guid entity, Guid vendor, string number)
    {
        var body = new
        {
            legalEntityId = entity, vendorId = vendor,
            data = new
            {
                invoiceNumber = number, invoiceDate = TestDates.Iso(TestDates.Ago(10)), dueDate = (string?)null, currencyCode = "SAR", subtotal = 100m, taxTotal = 15m, total = 115m,
                poReference = (string?)null, supplierTaxInvoiceRef = (string?)null,
                lines = new[] { new { description = "Consulting", quantity = 1m, unitPrice = 100m, lineAmount = 100m, taxCode = "S", taxRate = 0.15m, taxAmount = 15m, poLineRef = (string?)null, dimensionsJson = (string?)null } },
            },
        };
        var created = await clerk.Post("/api/v1/invoices", body);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var submitted = await clerk.Post($"/api/v1/invoices/{id}/submit");
        Assert.Equal("Verified", (await submitted.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
        return id;
    }

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
        private Task<HttpResponseMessage> Send(HttpMethod m, string url, object? body)
        {
            var request = new HttpRequestMessage(m, url) { Content = JsonContent.Create(body ?? new { }) };
            request.Headers.Add("X-XSRF-TOKEN", Csrf);
            return Client.SendAsync(request);
        }

        public Task<HttpResponseMessage> Post(string url, object? body = null) => Send(HttpMethod.Post, url, body);

        public Task<HttpResponseMessage> Put(string url, object body) => Send(HttpMethod.Put, url, body);
    }
}
