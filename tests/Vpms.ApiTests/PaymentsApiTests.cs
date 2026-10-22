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
public sealed class PaymentsApiTests : IClassFixture<ApiFixtureHolder>
{
    private static readonly Guid Maker = Guid.NewGuid();
    private static readonly Guid Checker = Guid.NewGuid();
    private static readonly Guid BankMaker = Guid.NewGuid();
    private static readonly Guid BankChecker = Guid.NewGuid();
    private readonly ApiFixture _api;

    public PaymentsApiTests(ApiFixtureHolder holder) => _api = holder.Api;

    private sealed record Setup(string Iban, Guid Entity, Guid Vendor, Session Clerk, Session Approver, Session Preparer, Session PayApprover, Session Releaser);

    /// <summary>An entity with an active vendor, verified bank details, a one-step invoice workflow and one person per role.</summary>
    private async Task<Setup> Prepare(string code)
    {
        var entity = await ActiveVendorKit.CreateEntityAsync(_api.Services, _api.TenantA, Maker, code);
        var vendor = await ActiveVendorKit.ActiveVendorAsync(_api.Services, _api.TenantA, entity, "Vendor " + code, Maker, Checker);
        var iban = ActiveVendorKit.NewIban();
        await ActiveVendorKit.VerifiedBankDetailsAsync(_api.Services, _api.TenantA, vendor, BankMaker, BankChecker, "SAR", iban);
        var clerk = await SignIn($"{code}-clerk@acme.com", "Invoice Clerk");
        var approver = await SignIn($"{code}-appr@acme.com", "Invoice Approver");
        var preparer = await SignIn($"{code}-prep@acme.com", "Payment Preparer");
        var payApprover = await SignIn($"{code}-papr@acme.com", "Payment Approver");
        var releaser = await SignIn($"{code}-rel@acme.com", "Payment Releaser");
        await ActiveVendorKit.AsAsync(_api.Services, _api.TenantA, Maker, async s =>
        {
            await s.GetRequiredService<IApprovalAdminService>().CreateAuthorityAsync(new CreateApprovalAuthority(entity, approver.UserId, 1_000_000m, "SAR", null, DateTimeOffset.UtcNow.AddDays(-1), null));
            return 0;
        });
        var admin = await SignIn($"{code}-wfa@acme.com", "Workflow Administrator");
        var changer = await SignIn($"{code}-wfc@acme.com", "Workflow Change Approver");
        var spec = new { name = "One", start = "s", steps = new object[] { new { id = "s", type = "Approval", approvers = new[] { new { kind = "User", value = approver.UserId.ToString() } } } } };
        var proposed = await admin.Put($"/api/v1/admin/legal-entities/{entity}/workflow", new { spec, fromMatrix = false, note = "initial" });
        var id = (await proposed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await changer.Post($"/api/v1/workflow-changes/{id}/approve", new { })).StatusCode);
        return new Setup(iban, entity, vendor, clerk, approver, preparer, payApprover, releaser);
    }

    private static async Task<Guid> ApprovedInvoice(Setup s, string number)
    {
        var body = new
        {
            legalEntityId = s.Entity, vendorId = s.Vendor,
            data = new
            {
                invoiceNumber = number, invoiceDate = TestDates.Iso(TestDates.Ago(10)), dueDate = TestDates.Iso(TestDates.Ago(10).AddDays(30)), currencyCode = "SAR", subtotal = 100m, taxTotal = 15m, total = 115m,
                poReference = (string?)null, supplierTaxInvoiceRef = (string?)null,
                lines = new[] { new { description = "Consulting", quantity = 1m, unitPrice = 100m, lineAmount = 100m, taxCode = "S", taxRate = 0.15m, taxAmount = 15m, poLineRef = (string?)null, dimensionsJson = (string?)null } },
            },
        };
        var created = await s.Clerk.Post("/api/v1/invoices", body);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        await s.Clerk.Post($"/api/v1/invoices/{id}/submit");
        await s.Clerk.Post($"/api/v1/invoices/{id}/send-for-approval");
        var task = (await s.Approver.Client.GetFromJsonAsync<JsonElement>("/api/v1/approvals/inbox")).EnumerateArray().Single(i => i.GetProperty("subjectId").GetGuid() == id).GetProperty("taskId").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await s.Approver.Post($"/api/v1/approvals/tasks/{task}/decision", new { decision = "Approve" })).StatusCode);
        return id;
    }

    [Fact]
    public async Task An_approved_invoice_is_paid_out_through_request_proposal_approval_release_and_download()
    {
        var s = await Prepare("PA1");
        var invoiceId = await ApprovedInvoice(s, "PAY-1");

        var payable = await s.Preparer.Client.GetFromJsonAsync<JsonElement>($"/api/v1/payments/payable?legalEntityId={s.Entity}");
        Assert.Equal(invoiceId, payable[0].GetProperty("invoiceId").GetGuid());
        Assert.Equal(0, payable[0].GetProperty("blockers").GetArrayLength());

        var created = await (await s.Preparer.Post("/api/v1/payments/requests", new { invoiceIds = new[] { invoiceId } })).Content.ReadFromJsonAsync<JsonElement>();
        var requestId = created[0].GetProperty("request").GetProperty("id").GetGuid();

        var proposals = await (await s.Preparer.Post("/api/v1/payments/proposals", new { legalEntityId = s.Entity, requestIds = new[] { requestId } })).Content.ReadFromJsonAsync<JsonElement>();
        var proposalId = proposals[0].GetProperty("id").GetGuid();
        Assert.Equal("Draft", proposals[0].GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.OK, (await s.Preparer.Post($"/api/v1/payments/proposals/{proposalId}/submit")).StatusCode);

        // the maker cannot approve or release; the approver cannot release
        Assert.Equal(HttpStatusCode.NotFound, (await s.Preparer.Post($"/api/v1/payments/proposals/{proposalId}/approve", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await s.Releaser.Post($"/api/v1/payments/proposals/{proposalId}/approve", new { })).StatusCode);
        var approved = await s.PayApprover.Post($"/api/v1/payments/proposals/{proposalId}/approve", new { comment = "ok" });
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await s.PayApprover.Post($"/api/v1/payments/proposals/{proposalId}/release")).StatusCode);

        var recheck = await s.PayApprover.Client.GetFromJsonAsync<JsonElement>($"/api/v1/payments/proposals/{proposalId}/recheck");
        Assert.True(recheck.GetProperty("passed").GetBoolean());

        var released = await s.Releaser.Post($"/api/v1/payments/proposals/{proposalId}/release");
        Assert.Equal(HttpStatusCode.OK, released.StatusCode);
        var body = await released.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Released", body.GetProperty("proposal").GetProperty("status").GetString());
        var instructionId = body.GetProperty("instruction").GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.NotFound, (await s.Preparer.Client.GetAsync($"/api/v1/payments/instructions/{instructionId}/download")).StatusCode);
        var file = await s.Releaser.Client.GetAsync($"/api/v1/payments/instructions/{instructionId}/download?kind=csv");
        Assert.Equal(HttpStatusCode.OK, file.StatusCode);
        Assert.Equal("text/csv; charset=utf-8", file.Content.Headers.ContentType!.ToString());
        Assert.Equal("no-store", file.Headers.CacheControl!.ToString());
        Assert.Contains(s.Iban, await file.Content.ReadAsStringAsync());
        Assert.True((await s.Releaser.Client.GetFromJsonAsync<JsonElement>($"/api/v1/payments/instructions/{instructionId}/verify")).GetProperty("valid").GetBoolean());

        var instruction = await s.PayApprover.Client.GetFromJsonAsync<JsonElement>($"/api/v1/payments/proposals/{proposalId}/instruction");
        Assert.Equal("HandedOff", instruction.GetProperty("status").GetString());
        var attempts = await s.PayApprover.Client.GetFromJsonAsync<JsonElement>($"/api/v1/payments/proposals/{proposalId}/authorizations");
        Assert.Equal("Released", attempts[0].GetProperty("decision").GetString());
    }

    [Fact]
    public async Task Payment_endpoints_are_gated_by_permission_and_legal_entity()
    {
        var s = await Prepare("PA2");
        var other = await Prepare("PA2B");
        var invoiceId = await ApprovedInvoice(s, "PAY-2");

        Assert.Equal(HttpStatusCode.Forbidden, (await s.Clerk.Client.GetAsync("/api/v1/payments/payable")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Clerk.Client.GetAsync("/api/v1/payments/requests")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Releaser.Client.GetAsync("/api/v1/payments/payable")).StatusCode);

        // a preparer of another entity only cannot create requests for this entity's invoice
        var scoped = await SignIn("pa2-scoped@acme.com");
        await _api.CreateRoleAndAssign(_api.TenantA, "pa2-scoped@acme.com", "Other Entity Preparer", [Permissions.PaymentPrepare, Permissions.PaymentView], other.Entity);
        Assert.Equal(HttpStatusCode.NotFound, (await scoped.Post("/api/v1/payments/requests", new { invoiceIds = new[] { invoiceId } })).StatusCode);
        Assert.Equal(0, (await scoped.Client.GetFromJsonAsync<JsonElement>("/api/v1/payments/requests")).GetArrayLength());
    }

    [Fact]
    public async Task Errors_use_stable_codes()
    {
        var s = await Prepare("PA3");
        var invoiceId = await ApprovedInvoice(s, "PAY-3");
        var created = await (await s.Preparer.Post("/api/v1/payments/requests", new { invoiceIds = new[] { invoiceId } })).Content.ReadFromJsonAsync<JsonElement>();
        var requestId = created[0].GetProperty("request").GetProperty("id").GetGuid();

        var duplicate = await (await s.Preparer.Post("/api/v1/payments/requests", new { invoiceIds = new[] { invoiceId } })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("request_exists", duplicate[0].GetProperty("errorCode").GetString());

        var proposalId = (await (await s.Preparer.Post("/api/v1/payments/proposals", new { legalEntityId = s.Entity, requestIds = new[] { requestId } })).Content.ReadFromJsonAsync<JsonElement>())[0].GetProperty("id").GetGuid();
        var early = await s.Releaser.Post($"/api/v1/payments/proposals/{proposalId}/release");
        Assert.Equal(HttpStatusCode.Conflict, early.StatusCode);
        Assert.Equal("invalid_transition", await ErrorCode(early));

        var noReason = await s.Preparer.Post($"/api/v1/payments/proposals/{proposalId}/cancel", new { reason = " " });
        Assert.Equal("reason_required", await ErrorCode(noReason));
        var missing = await s.Preparer.Post("/api/v1/payments/proposals", new { legalEntityId = s.Entity, requestIds = new[] { Guid.NewGuid() } });
        Assert.Equal("request_not_available", await ErrorCode(missing));
    }

    [Fact]
    public async Task One_person_cannot_hold_payment_preparation_approval_and_release_roles_together()
    {
        await SignIn("pa4-both@acme.com", "Payment Preparer");
        await Assert.ThrowsAsync<SegregationOfDutiesException>(() => _api.AssignRoleAsync(_api.TenantA, "pa4-both@acme.com", "Payment Releaser", null));
        await Assert.ThrowsAsync<SegregationOfDutiesException>(() => _api.AssignRoleAsync(_api.TenantA, "pa4-both@acme.com", "Payment Approver", null));
        await SignIn("pa4-appr@acme.com", "Invoice Approver");
        await Assert.ThrowsAsync<SegregationOfDutiesException>(() => _api.AssignRoleAsync(_api.TenantA, "pa4-appr@acme.com", "Payment Releaser", null));
    }

    // --- helpers -------------------------------------------------------------------------------------------

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
