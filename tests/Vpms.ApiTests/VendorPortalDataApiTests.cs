using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Vpms.BuildingBlocks.Security;
using Vpms.TestSupport;

namespace Vpms.ApiTests;

/// <summary>Vendor users see and change only their own vendor's data: across vendors, across tenants, and across session types.</summary>
[Collection(SqlServerCollection.Name)]
public sealed class VendorPortalDataApiTests : IClassFixture<ApiFixtureHolder>
{
    private const string Password = "purple elephants dance quietly";
    private static readonly byte[] Pdf = Encoding.ASCII.GetBytes("%PDF-1.7\n%%EOF");
    private readonly ApiFixture _api;

    public VendorPortalDataApiTests(ApiFixtureHolder holder) => _api = holder.Api;

    [Fact]
    public async Task Every_portal_endpoint_needs_a_vendor_session()
    {
        var anon = _api.NewBrowser();
        var staff = await SignInStaff("pd-staff0@acme.com", "Vendor Manager");
        var id = Guid.NewGuid();
        foreach (var path in new[]
                 {
                     "profile", "entities", "documents", $"documents/{id}/file", "bank-details", "bank-detail-changes", "requests",
                 })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync($"/api/v1/portal/{path}")).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await staff.Client.GetAsync($"/api/v1/portal/{path}")).StatusCode);
        }
    }

    [Fact]
    public async Task A_vendor_reads_and_edits_only_its_own_profile_within_limits()
    {
        var staff = await SignInStaff("pd-staff1@acme.com", "Vendor Manager");
        var one = await SignedInVendor(staff, "acme", "Isolation One", "pd1@one.example");
        var two = await SignedInVendor(staff, "acme", "Isolation Two", "pd1@two.example");

        var profile = await one.Client.GetFromJsonAsync<JsonElement>("/api/v1/portal/profile");
        Assert.Equal("Isolation One", profile.GetProperty("legalName").GetString());
        Assert.Equal(one.VendorId, profile.GetProperty("id").GetGuid());
        Assert.False(profile.TryGetProperty("taxId", out _));

        var update = await one.Put("/api/v1/portal/profile", new { tradeName = "One Trading", contactEmail = "ap@one.example", contactPhone = "+966500000001", legalName = "Hacked Name Ltd" });
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        var updated = await update.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("One Trading", updated.GetProperty("tradeName").GetString());
        Assert.Equal("Isolation One", updated.GetProperty("legalName").GetString()); // not editable by the vendor

        var bad = await one.Put("/api/v1/portal/profile", new { contactEmail = "not-an-email" });
        Assert.Equal("invalid_email", await ErrorCode(bad));

        // the other vendor's profile is untouched
        var other = await two.Client.GetFromJsonAsync<JsonElement>("/api/v1/portal/profile");
        Assert.Equal("Isolation Two", other.GetProperty("legalName").GetString());
        Assert.Equal(JsonValueKind.Null, other.GetProperty("tradeName").ValueKind);

        // vendor ids smuggled in the query string or headers change nothing
        var smuggled = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/portal/profile?vendorId={two.VendorId}");
        smuggled.Headers.Add("X-Vendor-Id", two.VendorId.ToString());
        var viaQuery = await (await one.Client.SendAsync(smuggled)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(one.VendorId, viaQuery.GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Entity_links_are_read_only_and_limited_to_the_vendors_own()
    {
        var staff = await SignInStaff("pd-staff2@acme.com", "Vendor Manager");
        var entityId = await CreateEntity(await SignInStaff("pd-admin2@acme.com", "Tenant Administrator"), "PD2");
        var one = await SignedInVendor(staff, "acme", "Links One", "pd2@one.example");
        var two = await SignedInVendor(staff, "acme", "Links Two", "pd2@two.example");

        var link = await staff.Put($"/api/v1/vendors/{one.VendorId}/entities/{entityId}", new { defaultCurrency = "SAR", paymentTermsCode = "N30", erpVendorAccount = "V-1001" });
        Assert.Equal(HttpStatusCode.OK, link.StatusCode);

        var mine = await one.Client.GetFromJsonAsync<JsonElement>("/api/v1/portal/entities");
        var row = Assert.Single(mine.EnumerateArray());
        Assert.Equal(entityId, row.GetProperty("legalEntityId").GetGuid());
        Assert.Equal("N30", row.GetProperty("paymentTermsCode").GetString());
        Assert.False(row.TryGetProperty("erpVendorAccount", out _)); // internal accounting data stays internal
        Assert.Empty((await two.Client.GetFromJsonAsync<JsonElement>("/api/v1/portal/entities")).EnumerateArray());

        // no write verbs exist for links
        Assert.Contains((await one.Put("/api/v1/portal/entities", new { })).StatusCode, new[] { HttpStatusCode.MethodNotAllowed, HttpStatusCode.Forbidden });
    }

    [Fact]
    public async Task Documents_are_private_to_the_vendor_that_uploaded_them()
    {
        var staff = await SignInStaff("pd-staff3@acme.com", "Vendor Manager");
        var one = await SignedInVendor(staff, "acme", "Docs One", "pd3@one.example");
        var two = await SignedInVendor(staff, "acme", "Docs Two", "pd3@two.example");
        var beta = await SignedInVendor(await SignInStaff("pd-staff3@beta.com", "Vendor Manager", beta: true), "beta", "Docs Beta", "pd3@beta.example");

        var upload = await one.Upload("TaxCertificate", "cert.pdf", Pdf);
        Assert.Equal(HttpStatusCode.OK, upload.StatusCode);
        var doc = await upload.Content.ReadFromJsonAsync<JsonElement>();
        var docId = doc.GetProperty("id").GetGuid();
        Assert.Equal("Pending", doc.GetProperty("scanStatus").GetString());

        // quarantined until clean
        Assert.Equal(HttpStatusCode.Conflict, (await one.Client.GetAsync($"/api/v1/portal/documents/{docId}/file")).StatusCode);
        await _api.ScanAllAsync();
        var file = await one.Client.GetAsync($"/api/v1/portal/documents/{docId}/file");
        Assert.Equal(HttpStatusCode.OK, file.StatusCode);
        Assert.Equal(Pdf, await file.Content.ReadAsByteArrayAsync());
        Assert.Equal("nosniff", file.Headers.GetValues("X-Content-Type-Options").Single());

        // another vendor in the same tenant and a vendor of another tenant: not found, and not listed
        foreach (var intruder in new[] { two, beta })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await intruder.Client.GetAsync($"/api/v1/portal/documents/{docId}/file")).StatusCode);
            Assert.Empty((await intruder.Client.GetFromJsonAsync<JsonElement>("/api/v1/portal/documents")).EnumerateArray());
        }

        Assert.Single((await one.Client.GetFromJsonAsync<JsonElement>("/api/v1/portal/documents")).EnumerateArray());

        // staff see the vendor's upload, marked as uploaded by a vendor
        var staffView = await staff.Client.GetFromJsonAsync<JsonElement>($"/api/v1/vendors/{one.VendorId}/documents");
        Assert.Single(staffView.EnumerateArray());

        // rejected uploads: unknown type, wrong content for the extension, infected file
        Assert.Equal("invalid_document_type", await ErrorCode(await one.Upload("Passport", "p.pdf", Pdf)));
        Assert.Equal(HttpStatusCode.BadRequest, (await one.Upload("Other", "fake.pdf", Encoding.ASCII.GetBytes("MZ not a pdf"))).StatusCode);
        var eicar = Encoding.ASCII.GetBytes("X5O!P%@AP[4\\PZX54(P^)7CC)7}$EICAR-STANDARD-ANTIVIRUS-TEST-FILE!$H+H*");
        var infected = await one.Upload("Other", "bad.pdf", [.. Pdf, .. eicar]);
        Assert.Equal(HttpStatusCode.OK, infected.StatusCode);
        var badId = (await infected.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        await _api.ScanAllAsync();
        Assert.NotEqual(HttpStatusCode.OK, (await one.Client.GetAsync($"/api/v1/portal/documents/{badId}/file")).StatusCode);
    }

    [Fact]
    public async Task A_vendor_bank_change_needs_staff_approval_with_a_callback_and_shows_the_vendor_nothing_internal()
    {
        var maker = await SignInStaff("pd-staff4@acme.com", "Vendor Manager");
        var checker = await SignInStaff("pd-checker4@acme.com", "Bank Details Approver");
        var one = await SignedInVendor(maker, "acme", "Bank One", "pd4@one.example");

        var iban = MakeIban("SA");
        var proposed = await one.Post("/api/v1/portal/bank-detail-changes", BankBody(iban));
        Assert.Equal(HttpStatusCode.OK, proposed.StatusCode);
        var text = await proposed.Content.ReadAsStringAsync();
        Assert.DoesNotContain(iban, text, StringComparison.Ordinal);
        var change = JsonDocument.Parse(text).RootElement;
        var changeId = change.GetProperty("id").GetGuid();
        Assert.Equal("Pending", change.GetProperty("status").GetString());
        Assert.False(change.TryGetProperty("madeBy", out _));
        Assert.False(change.TryGetProperty("checkerComment", out _));
        Assert.False(change.TryGetProperty("callbackVerificationRef", out _));

        // nothing is in effect yet
        Assert.DoesNotContain(await one.Client.GetFromJsonAsync<JsonElement>("/api/v1/portal/bank-details").ContinueWith(t => t.Result.EnumerateArray().ToList()),
            b => b.GetProperty("status").GetString() == "Verified");

        // staff see it as raised from the portal and must call the vendor back
        var queue = await checker.Client.GetFromJsonAsync<JsonElement>("/api/v1/bank-detail-changes?status=Pending");
        var queued = queue.EnumerateArray().Single(c => c.GetProperty("id").GetGuid() == changeId);
        Assert.Equal("VendorPortal", queued.GetProperty("source").GetString());
        Assert.True(queued.GetProperty("requiresCallback").GetBoolean());

        var noCallback = await checker.Post($"/api/v1/bank-detail-changes/{changeId}/approve", new { });
        Assert.Equal("callback_required", await ErrorCode(noCallback));
        Assert.Equal(HttpStatusCode.OK, (await checker.Post($"/api/v1/bank-detail-changes/{changeId}/approve", new { callbackVerificationRef = "CB-77 called finance on file number" })).StatusCode);

        var mine = await one.Client.GetFromJsonAsync<JsonElement>("/api/v1/portal/bank-details");
        var active = Assert.Single(mine.EnumerateArray());
        Assert.Equal("Verified", active.GetProperty("status").GetString());
        Assert.Contains(iban[^4..], active.GetProperty("maskedAccount").GetString());
        Assert.False(active.TryGetProperty("iban", out _));

        // a vendor can neither approve nor reveal
        Assert.Equal(HttpStatusCode.Unauthorized, (await one.Post($"/api/v1/bank-detail-changes/{changeId}/approve", new { callbackVerificationRef = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await one.Post($"/api/v1/bank-details/{active.GetProperty("id").GetGuid()}/reveal", new { })).StatusCode);
    }

    [Fact]
    public async Task Bank_changes_of_one_vendor_are_invisible_and_untouchable_for_another()
    {
        var staff = await SignInStaff("pd-staff5@acme.com", "Vendor Manager");
        var one = await SignedInVendor(staff, "acme", "Bank Iso One", "pd5@one.example");
        var two = await SignedInVendor(staff, "acme", "Bank Iso Two", "pd5@two.example");

        var changeId = (await (await one.Post("/api/v1/portal/bank-detail-changes", BankBody(MakeIban("SA")))).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        Assert.Empty((await two.Client.GetFromJsonAsync<JsonElement>("/api/v1/portal/bank-detail-changes")).EnumerateArray());
        Assert.Empty((await two.Client.GetFromJsonAsync<JsonElement>("/api/v1/portal/bank-details")).EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await two.Post($"/api/v1/portal/bank-detail-changes/{changeId}/cancel", new { })).StatusCode);

        // a vendor cannot withdraw a request by guessing; the owner can
        Assert.Single((await one.Client.GetFromJsonAsync<JsonElement>("/api/v1/portal/bank-detail-changes")).EnumerateArray());
        var cancelled = await one.Post($"/api/v1/portal/bank-detail-changes/{changeId}/cancel", new { });
        Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);
        Assert.Equal("Cancelled", (await cancelled.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
    }

    [Fact]
    public async Task The_shared_account_control_still_works_for_vendor_requests_without_revealing_other_vendors()
    {
        var staff = await SignInStaff("pd-staff6@acme.com", "Vendor Manager");
        var one = await SignedInVendor(staff, "acme", "Shared One", "pd6@one.example");
        var two = await SignedInVendor(staff, "acme", "Shared Two", "pd6@two.example");

        var iban = MakeIban("SA");
        Assert.Equal(HttpStatusCode.OK, (await one.Post("/api/v1/portal/bank-detail-changes", BankBody(iban))).StatusCode);

        // vendor two proposes the same account: refused, with no hint that another vendor uses it
        var clash = await two.Post("/api/v1/portal/bank-detail-changes", BankBody(iban) with { ConfirmSharedAccount = true });
        Assert.Equal(HttpStatusCode.Conflict, clash.StatusCode);
        var body = await clash.Content.ReadAsStringAsync();
        Assert.Equal("bank_details_not_accepted", JsonDocument.Parse(body).RootElement.GetProperty("errorCode").GetString());
        Assert.DoesNotContain("Shared One", body, StringComparison.Ordinal);
        Assert.DoesNotContain("another vendor", body, StringComparison.OrdinalIgnoreCase);
        Assert.Empty((await two.Client.GetFromJsonAsync<JsonElement>("/api/v1/portal/bank-detail-changes")).EnumerateArray());
    }

    [Fact]
    public async Task Request_threads_are_private_and_vendors_can_only_reply()
    {
        var staff = await SignInStaff("pd-staff7@acme.com", "Vendor Manager");
        var one = await SignedInVendor(staff, "acme", "Msg One", "pd7@one.example");
        var two = await SignedInVendor(staff, "acme", "Msg Two", "pd7@two.example");

        var created = await staff.Post($"/api/v1/vendors/{one.VendorId}/requests", new { subject = "Updated tax certificate", body = "Please upload the 2026 certificate." });
        var requestId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var list = await one.Client.GetFromJsonAsync<JsonElement>("/api/v1/portal/requests");
        var thread = Assert.Single(list.EnumerateArray());
        Assert.Equal("Updated tax certificate", thread.GetProperty("subject").GetString());
        Assert.Empty((await two.Client.GetFromJsonAsync<JsonElement>("/api/v1/portal/requests")).EnumerateArray());

        Assert.Equal(HttpStatusCode.NotFound, (await two.Post($"/api/v1/portal/requests/{requestId}/reply", new { body = "Not mine" })).StatusCode);

        var reply = await one.Post($"/api/v1/portal/requests/{requestId}/reply", new { body = "Uploaded, thank you." });
        Assert.Equal(HttpStatusCode.OK, reply.StatusCode);
        var updated = await reply.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Responded", updated.GetProperty("status").GetString());
        Assert.Equal(new[] { "ToVendor", "FromVendor" }, updated.GetProperty("messages").EnumerateArray().Select(m => m.GetProperty("direction").GetString()).ToArray());

        Assert.Equal("invalid_message", await ErrorCode(await one.Post($"/api/v1/portal/requests/{requestId}/reply", new { body = " " })));

        await staff.Post($"/api/v1/vendors/{one.VendorId}/requests/{requestId}/close", new { });
        Assert.Equal("request_closed", await ErrorCode(await one.Post($"/api/v1/portal/requests/{requestId}/reply", new { body = "Too late" })));
    }

    // --- helpers -------------------------------------------------------------------------------------------

    private sealed record BankRequest(string AccountName, string BankName, string SwiftBic, string CountryCode, string CurrencyCode, string Iban, string Reason)
    {
        public bool ConfirmSharedAccount { get; init; }
    }

    private static BankRequest BankBody(string iban) => new("Vendor LLC", "National Bank", "NCBKSAJE", "SA", "SAR", iban, "New operating account");

    private static string MakeIban(string country)
    {
        const int length = 24;
        var bban = (Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N")).Where(char.IsDigit).Take(length - 4).Aggregate("", (a, c) => a + c).PadRight(length - 4, '7');
        var numeric = System.Numerics.BigInteger.Parse(bban + (country[0] - 'A' + 10) + (country[1] - 'A' + 10) + "00");
        return $"{country}{98 - (int)(numeric % 97):00}{bban}";
    }

    private string Code(string secret) => Totp.Compute(secret, Totp.StepFor(_api.Clock.GetUtcNow()));

    private async Task<StaffSession> SignInStaff(string email, string role, bool beta = false)
    {
        var client = _api.NewBrowser();
        var (idp, provider, tenant) = beta ? (_api.IdpB, _api.ProviderB, _api.TenantB) : (_api.IdpA, _api.ProviderA, _api.TenantA);
        idp.NextUser = new FakeUser("sub-" + email, email, email.Split('@')[0]);
        await AuthenticationTests.SignInAsync(client, idp, provider, "/");
        await _api.AssignRoleAsync(tenant, email, role, null);
        var csrf = (await client.GetFromJsonAsync<JsonElement>("/api/v1/session")).GetProperty("csrfToken").GetString()!;
        return new StaffSession(client, csrf);
    }

    private static async Task<Guid> CreateEntity(StaffSession admin, string code)
    {
        var created = await admin.Post("/api/v1/admin/legal-entities", new { code, legalName = code, countryCode = "SA", baseCurrency = "SAR" });
        Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync());
        return (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    /// <summary>A vendor user with a live session, set up through the real invite → password → MFA flow.</summary>
    private async Task<VendorSession> SignedInVendor(StaffSession staff, string tenantSlug, string vendorName, string email)
    {
        var created = await staff.Post("/api/v1/vendors", new { legalName = vendorName, countryCode = "SA" });
        var vendorId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var invite = await staff.Post($"/api/v1/vendors/{vendorId}/portal-users", new { email, displayName = "AP Clerk" });
        var url = (await invite.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("inviteUrl").GetString()!;
        var token = url[(url.IndexOf("token=", StringComparison.Ordinal) + 6)..];

        var client = _api.NewBrowser();
        var accept = await client.PostAsJsonAsync("/portal/auth/invite/accept", new { tenant = tenantSlug, token, password = Password });
        Assert.True(accept.IsSuccessStatusCode, await accept.Content.ReadAsStringAsync());
        var secret = (await accept.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("enrollment").GetProperty("secret").GetString()!;
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/portal/auth/invite/enroll", new { tenant = tenantSlug, token, code = Code(secret) })).StatusCode);
        var csrf = (await client.GetFromJsonAsync<JsonElement>("/api/v1/portal/session")).GetProperty("csrfToken").GetString()!;
        return new VendorSession(client, csrf, vendorId);
    }

    private static async Task<string?> ErrorCode(HttpResponseMessage r) => (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errorCode").GetString();

    private static HttpRequestMessage WithToken(HttpMethod method, string url, string csrf, HttpContent? content)
    {
        var request = new HttpRequestMessage(method, url) { Content = content };
        request.Headers.Add("X-XSRF-TOKEN", csrf);
        return request;
    }

    private sealed record StaffSession(HttpClient Client, string Csrf)
    {
        public Task<HttpResponseMessage> Post(string url, object body) => Client.SendAsync(WithToken(HttpMethod.Post, url, Csrf, JsonContent.Create(body)));

        public Task<HttpResponseMessage> Put(string url, object body) => Client.SendAsync(WithToken(HttpMethod.Put, url, Csrf, JsonContent.Create(body)));
    }

    private sealed record VendorSession(HttpClient Client, string Csrf, Guid VendorId)
    {
        public Task<HttpResponseMessage> Post(string url, object body) => Client.SendAsync(WithToken(HttpMethod.Post, url, Csrf, JsonContent.Create(body)));

        public Task<HttpResponseMessage> Put(string url, object body) => Client.SendAsync(WithToken(HttpMethod.Put, url, Csrf, JsonContent.Create(body)));

        public Task<HttpResponseMessage> Upload(string type, string fileName, byte[] bytes)
        {
            var form = new MultipartFormDataContent { { new StringContent(type), "type" }, { new ByteArrayContent(bytes), "file", fileName } };
            return Client.SendAsync(WithToken(HttpMethod.Post, "/api/v1/portal/documents", Csrf, form));
        }
    }
}
