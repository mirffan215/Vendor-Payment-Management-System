using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Vpms.TestSupport;

namespace Vpms.ApiTests;

[Collection(SqlServerCollection.Name)]
public sealed class VendorApiTests : IClassFixture<ApiFixtureHolder>
{
    private static readonly byte[] Pdf = Encoding.ASCII.GetBytes("%PDF-1.7\n%%EOF");
    private readonly ApiFixture _api;

    public VendorApiTests(ApiFixtureHolder holder) => _api = holder.Api;

    [Fact]
    public async Task Anonymous_and_unauthorised_callers_are_rejected()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await _api.NewBrowser().GetAsync("/api/v1/vendors")).StatusCode);
        var plain = await SignIn("vendor-plain@acme.com");
        Assert.Equal(HttpStatusCode.Forbidden, (await plain.Client.GetAsync("/api/v1/vendors")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await plain.Post("/api/v1/vendors", new { legalName = "X", countryCode = "SA" })).StatusCode);
    }

    [Fact]
    public async Task A_vendor_goes_through_onboarding_with_a_second_person_approving()
    {
        var manager = await SignIn("vm1@acme.com", "Vendor Manager");
        var approver = await SignIn("va1@acme.com", "Vendor Approver");

        var created = await manager.Post("/api/v1/vendors", new { legalName = "Gulf Parts Co", legalNameAr = "شركة الخليج", countryCode = "SA", taxId = "300055555500003", contactEmail = "ap@gulfparts.example" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var vendorId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        foreach (var type in new[] { "CommercialRegistration", "TaxCertificate" })
        {
            var upload = await manager.Upload($"/api/v1/vendors/{vendorId}/documents", type, "cert.pdf", Pdf);
            Assert.Equal(HttpStatusCode.OK, upload.StatusCode);
        }

        Assert.Equal(HttpStatusCode.OK, (await manager.Post($"/api/v1/vendors/{vendorId}/submit", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await approver.Post($"/api/v1/vendors/{vendorId}/start-review", new { })).StatusCode);

        var tooEarly = await approver.Post($"/api/v1/vendors/{vendorId}/approve", new { });
        Assert.Equal(HttpStatusCode.Conflict, tooEarly.StatusCode);
        Assert.Equal("documents_missing", await ErrorCode(tooEarly));

        // download is blocked until the scan has cleared the file
        var docs = await approver.Client.GetFromJsonAsync<JsonElement>($"/api/v1/vendors/{vendorId}/documents");
        var firstDoc = docs[0].GetProperty("id").GetGuid();
        var pending = await approver.Client.GetAsync($"/api/v1/vendors/{vendorId}/documents/{firstDoc}/file");
        Assert.Equal(HttpStatusCode.Conflict, pending.StatusCode);
        Assert.Equal("attachment_not_ready", await ErrorCode(pending));

        await _api.ScanAllAsync();
        var file = await approver.Client.GetAsync($"/api/v1/vendors/{vendorId}/documents/{firstDoc}/file");
        Assert.Equal(HttpStatusCode.OK, file.StatusCode);
        Assert.Equal(Pdf, await file.Content.ReadAsByteArrayAsync());
        Assert.Equal("nosniff", file.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("attachment", file.Content.Headers.ContentDisposition?.DispositionType);

        foreach (var d in docs.EnumerateArray())
        {
            Assert.Equal(HttpStatusCode.OK, (await approver.Post($"/api/v1/vendors/{vendorId}/documents/{d.GetProperty("id").GetGuid()}/verify", new { })).StatusCode);
        }

        var approved = await approver.Post($"/api/v1/vendors/{vendorId}/approve", new { });
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        Assert.Equal("Active", (await approved.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
    }

    [Fact]
    public async Task The_maker_cannot_approve_even_when_holding_both_roles_is_blocked_at_assignment()
    {
        var manager = await SignIn("vm2@acme.com", "Vendor Manager");
        // assigning the approver role to the same person is refused by segregation of duties
        await Assert.ThrowsAsync<Vpms.BuildingBlocks.Security.SegregationOfDutiesException>(() => _api.AssignRoleAsync(_api.TenantA, "vm2@acme.com", "Vendor Approver", null));

        var created = await manager.Post("/api/v1/vendors", new { legalName = "Maker Co", countryCode = "SA" });
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.Post($"/api/v1/vendors/{id}/approve", new { })).StatusCode);
    }

    [Fact]
    public async Task A_custom_role_cannot_be_used_to_combine_editing_and_approving()
    {
        var creator = await SignIn("vm3@acme.com", "Vendor Manager");
        var created = await creator.Post("/api/v1/vendors", new { legalName = "Self Approve Co", countryCode = "SA" });
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        await Assert.ThrowsAsync<Vpms.BuildingBlocks.Security.SegregationOfDutiesException>(() =>
            _api.CreateRoleAndAssign(_api.TenantA, "vm3@acme.com", "Vendor Admin Custom", [Vpms.Identity.Contracts.Permissions.VendorApprove], null));
        Assert.Equal(HttpStatusCode.Forbidden, (await creator.Post($"/api/v1/vendors/{id}/approve", new { })).StatusCode);
    }

    [Fact]
    public async Task Duplicate_vendors_are_flagged_with_a_clear_conflict()
    {
        var manager = await SignIn("vm4@acme.com", "Vendor Manager");
        await manager.Post("/api/v1/vendors", new { legalName = "Dup One", countryCode = "SA", taxId = "300077777700003" });
        var second = await manager.Post("/api/v1/vendors", new { legalName = "Dup Two", countryCode = "SA", taxId = "300077777700003" });
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal("vendor_duplicate", await ErrorCode(second));
    }

    [Fact]
    public async Task Hostile_uploads_are_rejected_with_problem_documents()
    {
        var manager = await SignIn("vm5@acme.com", "Vendor Manager");
        var id = (await (await manager.Post("/api/v1/vendors", new { legalName = "Upload Co", countryCode = "SA" })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var exe = await manager.Upload($"/api/v1/vendors/{id}/documents", "Other", "setup.exe", "MZ"u8.ToArray());
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, exe.StatusCode);
        Assert.Equal("unsupported_type", await ErrorCode(exe));

        var fake = await manager.Upload($"/api/v1/vendors/{id}/documents", "Other", "fake.pdf", "not a pdf"u8.ToArray());
        Assert.Equal(HttpStatusCode.BadRequest, fake.StatusCode);
        Assert.Equal("content_mismatch", await ErrorCode(fake));
    }

    [Fact]
    public async Task A_document_or_request_is_only_reachable_through_its_own_vendor()
    {
        var manager = await SignIn("vm7@acme.com", "Vendor Manager");
        var approver = await SignIn("va7@acme.com", "Vendor Approver");
        var first = (await (await manager.Post("/api/v1/vendors", new { legalName = "Owner Co", countryCode = "SA" })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var second = (await (await manager.Post("/api/v1/vendors", new { legalName = "Other Co", countryCode = "SA" })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var doc = (await (await manager.Upload($"/api/v1/vendors/{first}/documents", "TaxCertificate", "c.pdf", Pdf)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var request = (await (await manager.Post($"/api/v1/vendors/{first}/requests", new { subject = "Need a certificate", body = "Please upload." })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        // the right vendor in the route works; any other vendor answers as if the record does not exist
        Assert.Equal(HttpStatusCode.NotFound, (await approver.Post($"/api/v1/vendors/{second}/documents/{doc}/verify", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await approver.Post($"/api/v1/vendors/{second}/documents/{doc}/reject", new { reason = "no" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await manager.Post($"/api/v1/vendors/{second}/requests/{request}/reply", new { body = "hi" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await manager.Post($"/api/v1/vendors/{second}/requests/{request}/close", new { })).StatusCode);
        await _api.ScanAllAsync();
        Assert.Equal(HttpStatusCode.OK, (await approver.Post($"/api/v1/vendors/{first}/documents/{doc}/verify", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await manager.Post($"/api/v1/vendors/{first}/requests/{request}/close", new { })).StatusCode);
    }

    [Theory]
    [InlineData("skip=-1")]
    [InlineData("take=0")]
    [InlineData("take=5000")]
    [InlineData("skip=2000000000")]
    public async Task Paging_parameters_outside_their_bounds_are_refused(string query)
    {
        var manager = await SignIn("vm8@acme.com", "Vendor Manager");
        var response = await manager.Client.GetAsync($"/api/v1/vendors?{query}");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Overlong_text_is_a_client_error_not_a_server_error()
    {
        var manager = await SignIn("vm9@acme.com", "Vendor Manager");
        var response = await manager.Post("/api/v1/vendors", new { legalName = new string('x', 5000), countryCode = "SA", contactEmail = new string('a', 5000) + "@x.com" });
        Assert.InRange((int)response.StatusCode, 400, 499);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Another_tenants_staff_cannot_see_the_vendor()
    {
        var manager = await SignIn("vm6@acme.com", "Vendor Manager");
        var id = (await (await manager.Post("/api/v1/vendors", new { legalName = "Acme Only", countryCode = "SA" })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var other = await SignInB("vm@beta.com", "Vendor Manager");
        Assert.Equal(HttpStatusCode.NotFound, (await other.Client.GetAsync($"/api/v1/vendors/{id}")).StatusCode);
        var list = await other.Client.GetFromJsonAsync<JsonElement>("/api/v1/vendors");
        Assert.Empty(list.EnumerateArray());
    }

    [Fact]
    public async Task Entity_rules_are_changed_only_by_people_allowed_for_that_entity()
    {
        var admin = await SignIn("vlink-admin@acme.com", "Tenant Administrator");
        var e1 = await CreateEntity(admin, "VLINK-1");
        var e2 = await CreateEntity(admin, "VLINK-2");
        var manager = await SignIn("vlink-manager@acme.com");
        await _api.CreateRoleAndAssign(_api.TenantA, "vlink-manager@acme.com", "Entity Vendor Editor",
            [Vpms.Identity.Contracts.Permissions.VendorEdit, Vpms.Identity.Contracts.Permissions.VendorView], e1);
        var wide = await SignIn("vlink-wide@acme.com", "Vendor Manager");
        var vendorId = (await (await wide.Post("/api/v1/vendors", new { legalName = "Linked Co", countryCode = "SA" })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var body = new { defaultCurrency = "SAR", paymentTermsCode = "NET30" };
        Assert.Equal(HttpStatusCode.OK, (await manager.Put($"/api/v1/vendors/{vendorId}/entities/{e1}", body)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.Put($"/api/v1/vendors/{vendorId}/entities/{e2}", body)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await wide.Put($"/api/v1/vendors/{vendorId}/entities/{e2}", body)).StatusCode);
    }

    // --- helpers -------------------------------------------------------------------------------------------

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

    private static async Task<Guid> CreateEntity(Session s, string code)
    {
        var r = await s.Post("/api/v1/admin/legal-entities", new { code, legalName = code, countryCode = "SA", baseCurrency = "SAR" });
        return (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static async Task<string?> ErrorCode(HttpResponseMessage r) => (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errorCode").GetString();

    private sealed record Session(HttpClient Client, string Csrf)
    {
        public Task<HttpResponseMessage> Post(string url, object body) => Send(HttpMethod.Post, url, JsonContent.Create(body));

        public Task<HttpResponseMessage> Put(string url, object body) => Send(HttpMethod.Put, url, JsonContent.Create(body));

        public Task<HttpResponseMessage> Upload(string url, string type, string fileName, byte[] content)
        {
            var form = new MultipartFormDataContent { { new StringContent(type), "type" } };
            var file = new ByteArrayContent(content);
            file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            form.Add(file, "file", fileName);
            return Send(HttpMethod.Post, url, form);
        }

        private Task<HttpResponseMessage> Send(HttpMethod method, string url, HttpContent content)
        {
            var request = new HttpRequestMessage(method, url) { Content = content };
            request.Headers.Add("X-XSRF-TOKEN", Csrf);
            return Client.SendAsync(request);
        }
    }
}
