using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Vpms.BuildingBlocks.Security;
using Vpms.Identity.Contracts;
using Vpms.TestSupport;

namespace Vpms.ApiTests;

[Collection(SqlServerCollection.Name)]
public sealed class InvoicesApiTests : IClassFixture<ApiFixtureHolder>
{
    private static readonly byte[] Pdf = Encoding.ASCII.GetBytes("%PDF-1.7\n%%EOF");
    private readonly ApiFixture _api;
    private static readonly Guid Maker = Guid.NewGuid();
    private static readonly Guid Checker = Guid.NewGuid();

    public InvoicesApiTests(ApiFixtureHolder holder) => _api = holder.Api;

    [Fact]
    public async Task Anonymous_and_plain_users_cannot_use_invoices()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await _api.NewBrowser().GetAsync("/api/v1/invoices")).StatusCode);
        var plain = await SignIn("inv-plain@acme.com");
        Assert.Equal(HttpStatusCode.Forbidden, (await plain.Client.GetAsync("/api/v1/invoices")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await plain.Post("/api/v1/invoices", Body(Guid.NewGuid(), Guid.NewGuid()))).StatusCode);
    }

    [Fact]
    public async Task A_clerk_creates_edits_submits_and_sees_the_invoice()
    {
        var (entity, vendor) = await Master("API1");
        var clerk = await SignIn("inv-clerk1@acme.com", "Invoice Clerk");

        var created = await clerk.Post("/api/v1/invoices", Body(entity, vendor, "API-001"));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var json = await created.Content.ReadFromJsonAsync<JsonElement>();
        var id = json.GetProperty("id").GetGuid();
        Assert.Equal("Draft", json.GetProperty("status").GetString());
        Assert.Equal(1, json.GetProperty("version").GetInt32());

        var edited = await clerk.Put($"/api/v1/invoices/{id}", Data("API-001A"));
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        Assert.Equal(2, (await edited.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("version").GetInt32());

        var submitted = await clerk.Post($"/api/v1/invoices/{id}/submit", new { });
        Assert.Equal("Verified", (await submitted.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());

        var again = await clerk.Post($"/api/v1/invoices/{id}/submit", new { });
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("invalid_transition", await ErrorCode(again));
        Assert.Equal("invalid_transition", await ErrorCode(await clerk.Put($"/api/v1/invoices/{id}", Data("API-001B"))));

        var list = await clerk.Client.GetFromJsonAsync<JsonElement>($"/api/v1/invoices?search=API-001&legalEntityId={entity}");
        Assert.Single(list.EnumerateArray());
    }

    [Fact]
    public async Task Errors_use_stable_codes()
    {
        var (entity, vendor) = await Master("API2");
        var clerk = await SignIn("inv-clerk2@acme.com", "Invoice Clerk");

        Assert.Equal("invalid_currency", await ErrorCode(await clerk.Post("/api/v1/invoices", Body(entity, vendor, "X-1", currency: "sar"))));
        Assert.Equal("invalid_invoice_number", await ErrorCode(await clerk.Post("/api/v1/invoices", Body(entity, vendor, "---"))));
        Assert.Equal("invalid_amount", await ErrorCode(await clerk.Post("/api/v1/invoices", Body(entity, vendor, "X-2", total: -5))));

        var otherEntity = await ActiveVendorKit.CreateEntityAsync(_api.Services, _api.TenantA, Maker, "API2B");
        var notLinked = await clerk.Post("/api/v1/invoices", Body(otherEntity, vendor, "X-3"));
        Assert.Equal(HttpStatusCode.Conflict, notLinked.StatusCode);
        Assert.Equal("vendor_cannot_transact", await ErrorCode(notLinked));

        Assert.Equal(HttpStatusCode.NotFound, (await clerk.Client.GetAsync($"/api/v1/invoices/{Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task Permissions_are_scoped_to_the_legal_entity()
    {
        var (entityX, vendorX) = await Master("API3X");
        var (entityY, vendorY) = await Master("API3Y");
        var all = await SignIn("inv-all3@acme.com", "Invoice Clerk");
        var x = (await (await all.Post("/api/v1/invoices", Body(entityX, vendorX, "SCOPE-X"))).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var y = (await (await all.Post("/api/v1/invoices", Body(entityY, vendorY, "SCOPE-Y"))).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var onlyX = await SignIn("inv-onlyx3@acme.com");
        await _api.CreateRoleAndAssign(_api.TenantA, "inv-onlyx3@acme.com", "Entity X Clerk",
            [Permissions.InvoiceView, Permissions.InvoiceCreate, Permissions.InvoiceEdit, Permissions.EntityView], entityX);

        var list = await onlyX.Client.GetFromJsonAsync<JsonElement>("/api/v1/invoices?search=SCOPE-");
        Assert.Equal([x], list.EnumerateArray().Select(i => i.GetProperty("id").GetGuid()));

        Assert.Equal(HttpStatusCode.OK, (await onlyX.Client.GetAsync($"/api/v1/invoices/{x}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await onlyX.Client.GetAsync($"/api/v1/invoices/{y}")).StatusCode); // existence is not revealed
        Assert.Equal(HttpStatusCode.NotFound, (await onlyX.Post($"/api/v1/invoices/{y}/submit", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await onlyX.Put($"/api/v1/invoices/{y}", Data("HIJACK"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await onlyX.Post("/api/v1/invoices", Body(entityY, vendorY, "SCOPE-Y2"))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await onlyX.Post("/api/v1/invoices", Body(entityX, vendorX, "SCOPE-X2"))).StatusCode);
    }

    [Fact]
    public async Task Another_tenants_staff_see_nothing()
    {
        var (entity, vendor) = await Master("API4");
        var clerk = await SignIn("inv-clerk4@acme.com", "Invoice Clerk");
        var id = (await (await clerk.Post("/api/v1/invoices", Body(entity, vendor, "TENANT-1"))).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var other = await SignInB("inv-clerk4@beta.com", "Invoice Clerk");
        Assert.Empty((await other.Client.GetFromJsonAsync<JsonElement>("/api/v1/invoices")).EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await other.Client.GetAsync($"/api/v1/invoices/{id}")).StatusCode);
    }

    [Fact]
    public async Task Files_are_attached_quarantined_and_downloaded_only_when_clean()
    {
        var (entity, vendor) = await Master("API5");
        var clerk = await SignIn("inv-clerk5@acme.com", "Invoice Clerk");
        var id = (await (await clerk.Post("/api/v1/invoices", Body(entity, vendor, "FILE-1"))).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var upload = await clerk.Upload($"/api/v1/invoices/{id}/attachments", "Original", "scan.pdf", Pdf);
        Assert.Equal(HttpStatusCode.OK, upload.StatusCode);
        var link = await upload.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Pending", link.GetProperty("scanStatus").GetString());
        var linkId = link.GetProperty("id").GetGuid();

        Assert.NotEqual(HttpStatusCode.OK, (await clerk.Client.GetAsync($"/api/v1/invoices/{id}/attachments/{linkId}/file")).StatusCode);
        await _api.ScanAllAsync();
        var file = await clerk.Client.GetAsync($"/api/v1/invoices/{id}/attachments/{linkId}/file");
        Assert.Equal(HttpStatusCode.OK, file.StatusCode);
        Assert.Equal(Pdf, await file.Content.ReadAsByteArrayAsync());
        Assert.Equal("nosniff", file.Headers.GetValues("X-Content-Type-Options").Single());

        Assert.Equal("invalid_attachment_role", await ErrorCode(await clerk.Upload($"/api/v1/invoices/{id}/attachments", "Bogus", "x.pdf", Pdf)));
        Assert.Equal(HttpStatusCode.BadRequest, (await clerk.Upload($"/api/v1/invoices/{id}/attachments", "Supporting", "fake.pdf", Encoding.ASCII.GetBytes("MZ nope"))).StatusCode);
    }

    [Fact]
    public async Task One_person_cannot_hold_invoice_entry_and_approval()
    {
        await SignIn("inv-sod@acme.com", "Invoice Clerk");
        await Assert.ThrowsAsync<SegregationOfDutiesException>(() => _api.AssignRoleAsync(_api.TenantA, "inv-sod@acme.com", "Invoice Approver", null));
    }

    // --- helpers -------------------------------------------------------------------------------------------

    private async Task<(Guid Entity, Guid Vendor)> Master(string code)
    {
        var entity = await ActiveVendorKit.CreateEntityAsync(_api.Services, _api.TenantA, Maker, code);
        var vendor = await ActiveVendorKit.ActiveVendorAsync(_api.Services, _api.TenantA, entity, "Vendor " + code, Maker, Checker);
        return (entity, vendor);
    }

    private static object Data(string number, string currency = "SAR", decimal total = 115m) => new
    {
        invoiceNumber = number, invoiceDate = TestDates.Iso(TestDates.Ago(10)), dueDate = TestDates.Iso(TestDates.Ago(10).AddDays(30)), currencyCode = currency, subtotal = 100m, taxTotal = 15m, total,
        poReference = (string?)null, supplierTaxInvoiceRef = (string?)null,
        lines = new[] { new { description = "Consulting", quantity = 1m, unitPrice = 100m, lineAmount = 100m, taxCode = "S", taxRate = 0.15m, taxAmount = 15m, poLineRef = (string?)null, dimensionsJson = (string?)null } },
    };

    private static object Body(Guid entity, Guid vendor, string number = "INV-1", string currency = "SAR", decimal total = 115m) =>
        new { legalEntityId = entity, vendorId = vendor, data = Data(number, currency, total) };

    private Task<Session> SignIn(string email, string? role = null) => SignInTo(_api.IdpA, _api.ProviderA, _api.TenantA, email, role);

    private Task<Session> SignInB(string email, string? role = null) => SignInTo(_api.IdpB, _api.ProviderB, _api.TenantB, email, role);

    private async Task<Session> SignInTo(FakeOidcProvider idp, Guid provider, Guid tenant, string email, string? role)
    {
        var client = _api.NewBrowser();
        idp.NextUser = new FakeUser("sub-" + email, email, email.Split('@')[0]);
        await AuthenticationTests.SignInAsync(client, idp, provider, "/");
        if (role is not null) await _api.AssignRoleAsync(tenant, email, role, null);
        var csrf = (await client.GetFromJsonAsync<JsonElement>("/api/v1/session")).GetProperty("csrfToken").GetString()!;
        return new Session(client, csrf);
    }

    private static async Task<string?> ErrorCode(HttpResponseMessage r) => (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errorCode").GetString();

    private sealed record Session(HttpClient Client, string Csrf)
    {
        private Task<HttpResponseMessage> Send(HttpMethod m, string url, HttpContent content)
        {
            var request = new HttpRequestMessage(m, url) { Content = content };
            request.Headers.Add("X-XSRF-TOKEN", Csrf);
            return Client.SendAsync(request);
        }

        public Task<HttpResponseMessage> Post(string url, object body) => Send(HttpMethod.Post, url, JsonContent.Create(body));

        public Task<HttpResponseMessage> Put(string url, object body) => Send(HttpMethod.Put, url, JsonContent.Create(body));

        public Task<HttpResponseMessage> Upload(string url, string role, string name, byte[] bytes) =>
            Send(HttpMethod.Post, url, new MultipartFormDataContent { { new StringContent(role), "role" }, { new ByteArrayContent(bytes), "file", name } });
    }
}
