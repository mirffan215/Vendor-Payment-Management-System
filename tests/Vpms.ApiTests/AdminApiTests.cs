using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Vpms.TestSupport;

namespace Vpms.ApiTests;

[Collection(SqlServerCollection.Name)]
public sealed class AdminApiTests : IClassFixture<ApiFixtureHolder>
{
    private readonly ApiFixture _api;

    public AdminApiTests(ApiFixtureHolder holder) => _api = holder.Api;

    [Fact]
    public async Task Anonymous_callers_are_rejected()
    {
        foreach (var path in new[] { "/api/v1/admin/legal-entities", "/api/v1/admin/roles", "/api/v1/admin/users", "/api/v1/admin/identity-providers", "/api/v1/admin/tenant" })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await _api.NewBrowser().GetAsync(path)).StatusCode);
        }
    }

    [Fact]
    public async Task Users_without_the_permission_get_403_problem_documents()
    {
        var user = await SignIn("plain@acme.com");
        var response = await user.PostJson("/api/v1/admin/legal-entities", new { code = "X1", legalName = "X", countryCode = "SA", baseCurrency = "SAR" });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await user.Client.GetAsync("/api/v1/admin/roles")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await user.Client.GetAsync("/api/v1/admin/identity-providers")).StatusCode);
    }

    [Fact]
    public async Task State_changing_admin_calls_need_the_antiforgery_header()
    {
        var admin = await SignIn("csrf-admin@acme.com", "Tenant Administrator");
        var response = await admin.Client.PostAsJsonAsync("/api/v1/admin/legal-entities", new { code = "NOCSRF", legalName = "X", countryCode = "SA", baseCurrency = "SAR" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Administrators_manage_legal_entities_with_clear_errors()
    {
        var admin = await SignIn("entities-admin@acme.com", "Tenant Administrator");
        var created = await admin.PostJson("/api/v1/admin/legal-entities",
            new { code = "ent-1", legalName = "Acme KSA", legalNameAr = "أكمي", countryCode = "SA", baseCurrency = "SAR", taxRegistrationNo = "300012345600003" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var entity = await created.Content.ReadFromJsonAsync<JsonElement>();
        var id = entity.GetProperty("id").GetGuid();
        Assert.Equal("ENT-1", entity.GetProperty("code").GetString());
        Assert.Equal("••••003", entity.GetProperty("taxRegistrationNoMasked").GetString());
        Assert.DoesNotContain("300012345600003", await created.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var duplicate = await admin.PostJson("/api/v1/admin/legal-entities", new { code = "ENT-1", legalName = "Dup", countryCode = "SA", baseCurrency = "SAR" });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal("entity_code_exists", await ErrorCode(duplicate));

        var invalid = await admin.PostJson("/api/v1/admin/legal-entities", new { code = "ENT-2", legalName = "Bad", countryCode = "SAU", baseCurrency = "SAR" });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal("invalid_country", await ErrorCode(invalid));

        var renamed = await admin.PutJson($"/api/v1/admin/legal-entities/{id}", new { legalName = "Acme KSA Renamed" });
        Assert.Equal("Acme KSA Renamed", (await renamed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("legalName").GetString());
        var off = await admin.PostJson($"/api/v1/admin/legal-entities/{id}/deactivate", new { });
        Assert.Equal("Inactive", (await off.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Entity_scoped_managers_can_only_change_their_own_entity()
    {
        var admin = await SignIn("scope-admin@acme.com", "Tenant Administrator");
        var e1 = await CreateEntity(admin, "SCOPE-1");
        var e2 = await CreateEntity(admin, "SCOPE-2");

        var manager = await SignIn("scoped-manager@acme.com");
        await _api.CreateRoleAndAssign(_api.TenantA, "scoped-manager@acme.com", "Entity Editor", [Vpms.Identity.Contracts.Permissions.EntityManage, Vpms.Identity.Contracts.Permissions.EntityView], e1);

        Assert.Equal(HttpStatusCode.OK, (await manager.PutJson($"/api/v1/admin/legal-entities/{e1}", new { legalName = "Mine" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.PutJson($"/api/v1/admin/legal-entities/{e2}", new { legalName = "Not mine" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.PostJson("/api/v1/admin/legal-entities", new { code = "SCOPE-3", legalName = "New", countryCode = "SA", baseCurrency = "SAR" })).StatusCode);

        var listed = await manager.Client.GetFromJsonAsync<JsonElement>("/api/v1/admin/legal-entities");
        Assert.Equal([e1.ToString()], listed.EnumerateArray().Select(x => x.GetProperty("id").GetString()!));
    }

    [Fact]
    public async Task Another_tenants_administrator_cannot_see_or_touch_these_entities()
    {
        var admin = await SignIn("iso-admin@acme.com", "Tenant Administrator");
        var id = await CreateEntity(admin, "ISO-1");

        var other = await SignInB("iso-admin@beta.com", "Tenant Administrator");
        Assert.Equal(HttpStatusCode.NotFound, (await other.Client.GetAsync($"/api/v1/admin/legal-entities/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PutJson($"/api/v1/admin/legal-entities/{id}", new { legalName = "Hijack" })).StatusCode);
        var list = await other.Client.GetFromJsonAsync<JsonElement>("/api/v1/admin/legal-entities");
        Assert.DoesNotContain(list.EnumerateArray(), e => e.GetProperty("id").GetGuid() == id);
    }

    [Fact]
    public async Task Role_administration_enforces_segregation_of_duties_over_http()
    {
        var admin = await SignIn("roles-admin@acme.com", "Tenant Administrator");
        var target = await SignIn("roles-target@acme.com");
        var session = await admin.Client.GetFromJsonAsync<JsonElement>("/api/v1/session");
        var adminId = session.GetProperty("userId").GetGuid();
        var targetId = (await target.Client.GetFromJsonAsync<JsonElement>("/api/v1/session")).GetProperty("userId").GetGuid();

        var conflicting = await admin.PostJson("/api/v1/admin/roles", new { name = "Everything", permissions = new[] { "Invoice.Approve", "Payment.Release" } });
        Assert.Equal(HttpStatusCode.Forbidden, conflicting.StatusCode);
        Assert.Equal("sod_violation", await ErrorCode(conflicting));

        var role = await (await admin.PostJson("/api/v1/admin/roles", new { name = "Report Reader " + Guid.NewGuid().ToString("N")[..6], permissions = new[] { "Audit.Read" } })).Content.ReadFromJsonAsync<JsonElement>();
        var roleId = role.GetProperty("id").GetGuid();

        var self = await admin.PostJson("/api/v1/admin/role-assignments", new { userId = adminId, roleId });
        Assert.Equal(HttpStatusCode.Forbidden, self.StatusCode);
        Assert.Equal("maker-checker", (await self.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("rule").GetString());

        var ok = await admin.PostJson("/api/v1/admin/role-assignments", new { userId = targetId, roleId });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await target.Client.GetAsync("/api/v1/audit/events")).StatusCode); // takes effect immediately
    }

    [Fact]
    public async Task Identity_provider_responses_never_contain_secrets_and_bad_input_is_explained()
    {
        var admin = await SignIn("providers-admin@acme.com", "Tenant Administrator");
        var created = await admin.PostJson("/api/v1/admin/identity-providers", new
        {
            kind = 3, displayName = "Partner SSO", authority = "https://idp.partner.test", clientId = "c1",
            clientSecretRef = "partner-sso-secret", autoProvision = true, allowedEmailDomains = new[] { "partner.test" },
        });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var body = await created.Content.ReadAsStringAsync();
        Assert.Contains("partner-sso-secret", body, StringComparison.Ordinal); // the reference, not a value
        Assert.Contains("\"secretAvailable\":false", body, StringComparison.Ordinal);

        var bad = await admin.PostJson("/api/v1/admin/identity-providers", new
        {
            kind = 2, displayName = "Insecure", authority = "http://insecure.test", clientId = "c", clientSecretRef = "s",
        });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal("invalid_authority", await ErrorCode(bad));
    }

    [Fact]
    public async Task Tenant_profile_is_readable_by_anyone_signed_in_and_editable_with_the_permission()
    {
        var plain = await SignIn("profile-user@acme.com");
        Assert.Equal(HttpStatusCode.OK, (await plain.Client.GetAsync("/api/v1/admin/tenant")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await plain.PutJson("/api/v1/admin/tenant", new { name = "Hacked", defaultLocale = "en", defaultCalendar = "gregorian" })).StatusCode);

        var admin = await SignIn("profile-admin@acme.com", "Tenant Administrator");
        var updated = await admin.PutJson("/api/v1/admin/tenant", new { name = "Acme Holding", defaultLocale = "ar", defaultCalendar = "hijri", primaryColor = "#0B5CAD" });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        Assert.Equal("hijri", (await updated.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("defaultCalendar").GetString());
    }

    [Fact]
    public async Task The_permission_catalogue_is_available_to_role_managers()
    {
        var admin = await SignIn("catalogue-admin@acme.com", "Tenant Administrator");
        var list = await admin.Client.GetFromJsonAsync<string[]>("/api/v1/admin/permissions");
        Assert.Contains("Vendor.BankDetails.Approve", list!);
        Assert.Contains("Audit.Read", list!);
    }

    // --- helpers -------------------------------------------------------------------------------------------

    private Task<Browser> SignIn(string email, string? role = null) => SignInTo(_api.IdpA, _api.ProviderA, _api.TenantA, email, role);

    private Task<Browser> SignInB(string email, string? role = null) => SignInTo(_api.IdpB, _api.ProviderB, _api.TenantB, email, role);

    private async Task<Browser> SignInTo(FakeOidcProvider idp, Guid provider, Guid tenant, string email, string? role)
    {
        var client = _api.NewBrowser();
        idp.NextUser = new FakeUser("sub-" + email, email, email.Split('@')[0]);
        await AuthenticationTests.SignInAsync(client, idp, provider, "/");
        if (role is not null)
        {
            await _api.AssignRoleAsync(tenant, email, role, null);
        }

        var csrf = (await client.GetFromJsonAsync<JsonElement>("/api/v1/session")).GetProperty("csrfToken").GetString()!;
        return new Browser(client, csrf);
    }

    private static async Task<Guid> CreateEntity(Browser admin, string code)
    {
        var response = await admin.PostJson("/api/v1/admin/legal-entities", new { code, legalName = code, countryCode = "SA", baseCurrency = "SAR" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static async Task<string?> ErrorCode(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errorCode").GetString();

    private sealed record Browser(HttpClient Client, string Csrf)
    {
        public Task<HttpResponseMessage> PostJson(string url, object body) => Send(HttpMethod.Post, url, body);

        public Task<HttpResponseMessage> PutJson(string url, object body) => Send(HttpMethod.Put, url, body);

        private Task<HttpResponseMessage> Send(HttpMethod method, string url, object body)
        {
            var request = new HttpRequestMessage(method, url) { Content = JsonContent.Create(body) };
            request.Headers.Add("X-XSRF-TOKEN", Csrf);
            return Client.SendAsync(request);
        }
    }
}
