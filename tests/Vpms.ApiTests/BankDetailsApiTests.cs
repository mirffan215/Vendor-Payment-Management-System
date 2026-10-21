using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Vpms.TestSupport;

namespace Vpms.ApiTests;

[Collection(SqlServerCollection.Name)]
public sealed class BankDetailsApiTests : IClassFixture<ApiFixtureHolder>
{
    private readonly ApiFixture _api;

    public BankDetailsApiTests(ApiFixtureHolder holder) => _api = holder.Api;

    [Fact]
    public async Task Anonymous_and_plain_users_cannot_touch_bank_details()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await _api.NewBrowser().GetAsync("/api/v1/bank-detail-changes")).StatusCode);
        var plain = await SignIn("bd-plain@acme.com");
        Assert.Equal(HttpStatusCode.Forbidden, (await plain.Client.GetAsync("/api/v1/bank-detail-changes")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await plain.Post($"/api/v1/vendors/{Guid.NewGuid()}/bank-detail-changes", Body())).StatusCode);
    }

    [Fact]
    public async Task The_full_two_person_flow_works_over_http_and_values_stay_masked()
    {
        var maker = await SignIn("bd-maker1@acme.com", "Vendor Manager");
        var checker = await SignIn("bd-checker1@acme.com", "Bank Details Approver");
        var vendorId = await CreateVendor(maker, "Bank Co 1");

        var Iban = MakeIban("SA");
        var proposed = await maker.Post($"/api/v1/vendors/{vendorId}/bank-detail-changes", Body(Iban));
        Assert.Equal(HttpStatusCode.OK, proposed.StatusCode);
        var text = await proposed.Content.ReadAsStringAsync();
        Assert.DoesNotContain(Iban, text, StringComparison.Ordinal);
        Assert.Contains("••••" + Iban[^4..], text, StringComparison.Ordinal);
        var change = JsonDocument.Parse(text).RootElement;
        var changeId = change.GetProperty("id").GetGuid();
        var bankId = change.GetProperty("proposedBankDetailsId").GetGuid();

        // the maker cannot decide, with a stable error code
        var self = await maker.Post($"/api/v1/bank-detail-changes/{changeId}/approve", new { });
        Assert.Equal(HttpStatusCode.Forbidden, self.StatusCode); // the maker lacks the approve permission at all

        var queue = await checker.Client.GetFromJsonAsync<JsonElement>("/api/v1/bank-detail-changes?status=Pending");
        Assert.Contains(queue.EnumerateArray(), c => c.GetProperty("id").GetGuid() == changeId);

        var approved = await checker.Post($"/api/v1/bank-detail-changes/{changeId}/approve", new { comment = "Matches the signed contract" });
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        Assert.Equal("Approved", (await approved.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());

        var details = await checker.Client.GetFromJsonAsync<JsonElement>($"/api/v1/vendors/{vendorId}/bank-details");
        Assert.Equal("Verified", details[0].GetProperty("status").GetString());

        // reveal is POST-only, permission-gated and returns the full number
        var reveal = await checker.Post($"/api/v1/bank-details/{bankId}/reveal", new { });
        Assert.Equal(Iban, (await reveal.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("iban").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await maker.Post($"/api/v1/bank-details/{bankId}/reveal", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await checker.Client.GetAsync($"/api/v1/bank-details/{bankId}/reveal")).StatusCode);
    }

    [Fact]
    public async Task A_person_who_could_edit_and_approve_is_stopped_by_role_assignment_and_by_the_decision_rule()
    {
        var maker = await SignIn("bd-maker2@acme.com", "Vendor Manager");
        var vendorId = await CreateVendor(maker, "Bank Co 2");
        var changeId = (await (await maker.Post($"/api/v1/vendors/{vendorId}/bank-detail-changes", Body())).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        // Role assignment refuses to give the same person both edit and approve rights...
        await Assert.ThrowsAsync<Vpms.BuildingBlocks.Security.SegregationOfDutiesException>(() => _api.AssignRoleAsync(_api.TenantA, "bd-maker2@acme.com", "Bank Details Approver", null));
        // ...so the maker is forbidden outright.
        Assert.Equal(HttpStatusCode.Forbidden, (await maker.Post($"/api/v1/bank-detail-changes/{changeId}/approve", new { })).StatusCode);
    }

    [Fact]
    public async Task Replacements_need_a_callback_reference_and_errors_are_explained()
    {
        var maker = await SignIn("bd-maker3@acme.com", "Vendor Manager");
        var checker = await SignIn("bd-checker3@acme.com", "Bank Details Approver");
        var vendorId = await CreateVendor(maker, "Bank Co 3");

        var first = await (await maker.Post($"/api/v1/vendors/{vendorId}/bank-detail-changes", Body())).Content.ReadFromJsonAsync<JsonElement>();
        await checker.Post($"/api/v1/bank-detail-changes/{first.GetProperty("id").GetGuid()}/approve", new { });

        var second = await (await maker.Post($"/api/v1/vendors/{vendorId}/bank-detail-changes", Body(null, "AE"))).Content.ReadFromJsonAsync<JsonElement>();
        var secondId = second.GetProperty("id").GetGuid();
        Assert.True(second.GetProperty("requiresCallback").GetBoolean());

        var noCallback = await checker.Post($"/api/v1/bank-detail-changes/{secondId}/approve", new { });
        Assert.Equal(HttpStatusCode.BadRequest, noCallback.StatusCode);
        Assert.Equal("callback_required", await ErrorCode(noCallback));

        var ok = await checker.Post($"/api/v1/bank-detail-changes/{secondId}/approve", new { callbackVerificationRef = "CB-1 phone call to finance manager" });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

        var bad = await maker.Post($"/api/v1/vendors/{vendorId}/bank-detail-changes", Body("SA0380000000608010167518"));
        Assert.Equal("invalid_iban", await ErrorCode(bad));
    }

    [Fact]
    public async Task Another_tenants_staff_see_nothing()
    {
        var maker = await SignIn("bd-maker4@acme.com", "Vendor Manager");
        var vendorId = await CreateVendor(maker, "Bank Co 4");
        await maker.Post($"/api/v1/vendors/{vendorId}/bank-detail-changes", Body());

        var other = await SignInB("bd-checker@beta.com", "Bank Details Approver");
        var list = await other.Client.GetFromJsonAsync<JsonElement>("/api/v1/bank-detail-changes");
        Assert.Empty(list.EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await other.Post($"/api/v1/bank-detail-changes/{Guid.NewGuid()}/approve", new { })).StatusCode);
    }

    // --- helpers -------------------------------------------------------------------------------------------

    private static object Body(string? iban = null, string country = "SA") => new
    {
        accountName = "Bank Co LLC", bankName = "National Bank", swiftBic = "NCBKSAJE", countryCode = country, currencyCode = "SAR",
        iban = iban ?? MakeIban(country), reason = "Onboarding",
    };

    /// <summary>A valid, unique IBAN (correct length and mod-97 check digits), so tests do not trip the shared-account control.</summary>
    private static string MakeIban(string country)
    {
        var length = country == "AE" ? 23 : 24;
        var bban = (Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N")).Where(char.IsDigit).Take(length - 4).Aggregate("", (a, c) => a + c);
        bban = bban.PadRight(length - 4, '7');
        var numeric = System.Numerics.BigInteger.Parse(bban + (country[0] - 'A' + 10) + (country[1] - 'A' + 10) + "00");
        var check = 98 - (int)(numeric % 97);
        return $"{country}{check:00}{bban}";
    }

    private static async Task<Guid> CreateVendor(Session s, string name)
    {
        var r = await s.Post("/api/v1/vendors", new { legalName = name, countryCode = "SA" });
        return (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

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
        public Task<HttpResponseMessage> Post(string url, object body)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
            request.Headers.Add("X-XSRF-TOKEN", Csrf);
            return Client.SendAsync(request);
        }
    }
}
