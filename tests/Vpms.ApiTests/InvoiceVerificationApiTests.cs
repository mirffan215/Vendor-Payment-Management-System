using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Vpms.Identity.Contracts;
using Vpms.TestSupport;

namespace Vpms.ApiTests;

[Collection(SqlServerCollection.Name)]
public sealed class InvoiceVerificationApiTests : IClassFixture<ApiFixtureHolder>
{
    private static readonly Guid Maker = Guid.NewGuid();
    private static readonly Guid Checker = Guid.NewGuid();
    private readonly ApiFixture _api;

    public InvoiceVerificationApiTests(ApiFixtureHolder holder) => _api = holder.Api;

    [Fact]
    public async Task Verification_runs_on_submit_and_the_queue_lets_a_reviewer_waive_a_flag()
    {
        var (entity, vendor) = await Master("VF1");
        var clerk = await SignIn("vf-clerk1@acme.com", "Invoice Clerk");

        var invoice = await Submit(clerk, entity, vendor, "VF-1", po: "PO-NOPE");
        Assert.Equal("Exception", invoice.GetProperty("status").GetString());
        var id = invoice.GetProperty("id").GetGuid();

        var detail = await clerk.Client.GetFromJsonAsync<JsonElement>($"/api/v1/invoices/{id}/verification");
        Assert.Equal(10, detail.GetProperty("results").GetArrayLength());
        var exception = detail.GetProperty("exceptions").EnumerateArray().Single();
        Assert.Equal("po.match", exception.GetProperty("ruleCode").GetString());
        Assert.Equal("Flag", exception.GetProperty("severity").GetString());
        Assert.Equal("po_not_found", exception.GetProperty("findings")[0].GetProperty("key").GetString());
        var exceptionId = exception.GetProperty("id").GetGuid();

        var queue = await clerk.Client.GetFromJsonAsync<JsonElement>("/api/v1/invoice-exceptions?status=Open");
        Assert.Contains(queue.EnumerateArray(), e => e.GetProperty("id").GetGuid() == exceptionId);

        Assert.Equal("reason_required", await ErrorCode(await clerk.Post($"/api/v1/invoice-exceptions/{exceptionId}/waive", new { reason = " " })));
        var waived = await clerk.Post($"/api/v1/invoice-exceptions/{exceptionId}/waive", new { reason = "PO is being created in the ERP today" });
        Assert.Equal(HttpStatusCode.OK, waived.StatusCode);
        Assert.Equal("Waived", (await waived.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
        Assert.Equal("Verified", (await clerk.Client.GetFromJsonAsync<JsonElement>($"/api/v1/invoices/{id}")).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Waiving_a_block_needs_the_override_permission_and_a_second_person()
    {
        var (entity, vendor) = await Master("VF2");
        var clerk = await SignIn("vf-clerk2@acme.com", "Invoice Clerk");
        await Submit(clerk, entity, vendor, "VF-DUP");
        var dup = await Submit(clerk, entity, vendor, "VF-DUP");
        Assert.Equal("Exception", dup.GetProperty("status").GetString());
        var exceptionId = (await clerk.Client.GetFromJsonAsync<JsonElement>($"/api/v1/invoices/{dup.GetProperty("id").GetGuid()}/verification"))
            .GetProperty("exceptions").EnumerateArray().Single(e => e.GetProperty("ruleCode").GetString() == "duplicate.exact").GetProperty("id").GetGuid();

        var denied = await clerk.Post($"/api/v1/invoice-exceptions/{exceptionId}/waive", new { reason = "I am sure" });
        Assert.Equal(HttpStatusCode.Conflict, denied.StatusCode);
        Assert.Equal("override_required", await ErrorCode(denied));

        var reviewer = await SignIn("vf-reviewer2@acme.com", "Invoice Exception Reviewer");
        var ok = await reviewer.Post($"/api/v1/invoice-exceptions/{exceptionId}/waive", new { reason = "Original was voided by the vendor" });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal("Verified", (await clerk.Client.GetFromJsonAsync<JsonElement>($"/api/v1/invoices/{dup.GetProperty("id").GetGuid()}")).GetProperty("status").GetString());
    }

    [Fact]
    public async Task The_queue_and_actions_are_scoped_to_the_legal_entity()
    {
        var (entityX, vendorX) = await Master("VF3X");
        var (entityY, vendorY) = await Master("VF3Y");
        var all = await SignIn("vf-all3@acme.com", "Invoice Clerk");
        var onY = await Submit(all, entityY, vendorY, "SCOPE-Y", po: "PO-NOPE");
        await Submit(all, entityX, vendorX, "SCOPE-X", po: "PO-NOPE");

        var onlyX = await SignIn("vf-onlyx3@acme.com");
        await _api.CreateRoleAndAssign(_api.TenantA, "vf-onlyx3@acme.com", "Entity X Resolver", [Permissions.InvoiceView, Permissions.InvoiceExceptionResolve], entityX);

        var queue = await onlyX.Client.GetFromJsonAsync<JsonElement>("/api/v1/invoice-exceptions?status=Open");
        var invoiceNumbers = queue.EnumerateArray().Select(e => e.GetProperty("invoiceNumber").GetString()).ToList();
        Assert.Contains("SCOPE-X", invoiceNumbers);
        Assert.DoesNotContain("SCOPE-Y", invoiceNumbers);

        var yException = (await all.Client.GetFromJsonAsync<JsonElement>($"/api/v1/invoices/{onY.GetProperty("id").GetGuid()}/verification")).GetProperty("exceptions")[0].GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NotFound, (await onlyX.Post($"/api/v1/invoice-exceptions/{yException}/waive", new { reason = "no" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await onlyX.Client.GetAsync($"/api/v1/invoices/{onY.GetProperty("id").GetGuid()}/verification")).StatusCode);

        var plain = await SignIn("vf-plain3@acme.com");
        Assert.Equal(HttpStatusCode.Forbidden, (await plain.Client.GetAsync("/api/v1/invoice-exceptions")).StatusCode);
    }

    [Fact]
    public async Task Purchase_orders_are_imported_by_people_with_the_import_right_and_a_reverify_clears_the_exception()
    {
        var (entity, vendor) = await Master("VF4");
        var clerk = await SignIn("vf-clerk4@acme.com", "Invoice Clerk");
        var importer = await SignIn("vf-importer4@acme.com", "Verification Administrator");
        var invoice = await Submit(clerk, entity, vendor, "VF-PO-1", po: "PO-API-1");
        Assert.Equal("Exception", invoice.GetProperty("status").GetString());

        var payload = new
        {
            purchaseOrders = new[]
            {
                new
                {
                    poNumber = "PO-API-1", version = 1, vendorId = vendor, erpVendorAccount = (string?)null, currencyCode = "SAR", status = "Open", orderDate = TestDates.Iso(TestDates.Ago(30)),
                    previouslyInvoicedValue = 0m, lines = new[] { new { lineNo = "1", description = "Consulting", orderedQuantity = 5m, unitPrice = 100m, previouslyInvoicedQuantity = 0m } },
                },
                new
                {
                    poNumber = "PO-BAD", version = 1, vendorId = vendor, erpVendorAccount = (string?)null, currencyCode = "sar", status = "Open", orderDate = TestDates.Iso(TestDates.Ago(30)),
                    previouslyInvoicedValue = 0m, lines = new[] { new { lineNo = "1", description = "x", orderedQuantity = 5m, unitPrice = 100m, previouslyInvoicedQuantity = 0m } },
                },
            },
        };
        Assert.Equal(HttpStatusCode.Forbidden, (await clerk.Post($"/api/v1/admin/legal-entities/{entity}/purchase-orders/import", payload)).StatusCode);
        var imported = await importer.Post($"/api/v1/admin/legal-entities/{entity}/purchase-orders/import", payload);
        Assert.Equal(HttpStatusCode.OK, imported.StatusCode);
        var result = await imported.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, result.GetProperty("created").GetInt32());
        Assert.Equal("invalid_currency", result.GetProperty("errors")[0].GetProperty("code").GetString());

        var orders = await clerk.Client.GetFromJsonAsync<JsonElement>($"/api/v1/purchase-orders?legalEntityId={entity}&search=PO-API");
        var order = Assert.Single(orders.EnumerateArray());
        Assert.Equal(HttpStatusCode.OK, (await clerk.Client.GetAsync($"/api/v1/purchase-orders/{order.GetProperty("id").GetGuid()}")).StatusCode);

        var reverified = await clerk.Post($"/api/v1/invoices/{invoice.GetProperty("id").GetGuid()}/reverify", new { });
        Assert.Equal(HttpStatusCode.OK, reverified.StatusCode);
        Assert.Equal("Verified", (await reverified.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("invoiceStatus").GetString());
    }

    // --- helpers -------------------------------------------------------------------------------------------

    private async Task<(Guid Entity, Guid Vendor)> Master(string code)
    {
        var entity = await ActiveVendorKit.CreateEntityAsync(_api.Services, _api.TenantA, Maker, code);
        var vendor = await ActiveVendorKit.ActiveVendorAsync(_api.Services, _api.TenantA, entity, "Vendor " + code, Maker, Checker);
        return (entity, vendor);
    }

    private static async Task<JsonElement> Submit(Session clerk, Guid entity, Guid vendor, string number, string? po = null)
    {
        var date = TestDates.Iso(TestDates.Ago(10));
        var body = new
        {
            legalEntityId = entity, vendorId = vendor,
            data = new
            {
                invoiceNumber = number, invoiceDate = date, dueDate = (string?)null, currencyCode = "SAR", subtotal = 100m, taxTotal = 15m, total = 115m, poReference = po, supplierTaxInvoiceRef = (string?)null,
                lines = new[] { new { description = "Consulting", quantity = 1m, unitPrice = 100m, lineAmount = 100m, taxCode = "S", taxRate = 0.15m, taxAmount = 15m, poLineRef = po is null ? null : "1", dimensionsJson = (string?)null } },
            },
        };
        var created = await clerk.Post("/api/v1/invoices", body);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var submitted = await clerk.Post($"/api/v1/invoices/{id}/submit", new { });
        Assert.Equal(HttpStatusCode.OK, submitted.StatusCode);
        return await submitted.Content.ReadFromJsonAsync<JsonElement>();
    }

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
        public Task<HttpResponseMessage> Post(string url, object body)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
            request.Headers.Add("X-XSRF-TOKEN", Csrf);
            return Client.SendAsync(request);
        }
    }
}
