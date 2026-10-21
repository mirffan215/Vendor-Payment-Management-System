using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Vpms.BuildingBlocks.Security;
using Vpms.TestSupport;

namespace Vpms.ApiTests;

[Collection(SqlServerCollection.Name)]
public sealed class VendorPortalApiTests : IClassFixture<ApiFixtureHolder>
{
    private const string Password = "purple elephants dance quietly";
    private readonly ApiFixture _api;

    public VendorPortalApiTests(ApiFixtureHolder holder) => _api = holder.Api;

    [Fact]
    public async Task A_vendor_user_signs_up_and_signs_in_with_password_and_authenticator()
    {
        var staff = await SignInStaff("portal-mgr1@acme.com");
        var vendorId = await CreateVendor(staff, "Portal Co 1");
        var (email, token) = await InviteUser(staff, vendorId, "ap1@portalco.example");

        var vendor = _api.NewBrowser();
        Assert.Equal(HttpStatusCode.Unauthorized, (await vendor.GetAsync("/api/v1/portal/session")).StatusCode);

        var info = await vendor.GetFromJsonAsync<JsonElement>($"/portal/auth/invite?tenant=acme&token={token}");
        Assert.Equal(email, info.GetProperty("email").GetString());

        var accept = await vendor.PostAsJsonAsync("/portal/auth/invite/accept", new { tenant = "acme", token, password = Password });
        Assert.Equal(HttpStatusCode.OK, accept.StatusCode);
        var secret = (await accept.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("enrollment").GetProperty("secret").GetString()!;

        var enroll = await vendor.PostAsJsonAsync("/portal/auth/invite/enroll", new { tenant = "acme", token, code = Code(secret) });
        Assert.Equal(HttpStatusCode.OK, enroll.StatusCode);
        Assert.Equal(10, (await enroll.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("recoveryCodes").GetArrayLength());

        var cookie = enroll.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("__Host-vpms-vendor=", StringComparison.Ordinal));
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);

        var session = await vendor.GetFromJsonAsync<JsonElement>("/api/v1/portal/session");
        Assert.Equal((vendorId, "Portal Co 1"), (session.GetProperty("vendorId").GetGuid(), session.GetProperty("vendorName").GetString()));

        // a second device: password, then the authenticator
        _api.Clock.Advance(TimeSpan.FromSeconds(31));
        var other = _api.NewBrowser();
        var login = await other.PostAsJsonAsync("/portal/auth/login", new { tenant = "acme", email, password = Password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await other.GetAsync("/api/v1/portal/session")).StatusCode); // password alone is not a session
        var challenge = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("challenge").GetString();
        var mfa = await other.PostAsJsonAsync("/portal/auth/mfa", new { challenge, code = Code(secret) });
        Assert.Equal(HttpStatusCode.NoContent, mfa.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await other.GetAsync("/api/v1/portal/session")).StatusCode);
    }

    [Fact]
    public async Task Staff_and_vendor_sessions_cannot_open_each_others_doors()
    {
        var staff = await SignInStaff("portal-mgr2@acme.com");
        var vendor = await SignedInVendor(staff, "Portal Co 2", "ap2@portalco.example");

        // vendor cookie against staff endpoints
        foreach (var path in new[] { "/api/v1/session", "/api/v1/vendors", "/api/v1/admin/legal-entities", "/api/v1/admin/roles", "/api/v1/audit/events", "/api/v1/bank-detail-changes" })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await vendor.Client.GetAsync(path)).StatusCode);
        }

        // staff cookie against the portal
        Assert.Equal(HttpStatusCode.Unauthorized, (await staff.Client.GetAsync("/api/v1/portal/session")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await staff.Client.GetAsync("/api/v1/session")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await vendor.Client.GetAsync("/api/v1/portal/session")).StatusCode);
    }

    [Fact]
    public async Task Signing_out_needs_the_antiforgery_token_and_ends_the_vendor_session()
    {
        var staff = await SignInStaff("portal-mgr3@acme.com");
        var vendor = await SignedInVendor(staff, "Portal Co 3", "ap3@portalco.example");

        var noToken = await vendor.Client.PostAsync("/portal/auth/logout", null);
        Assert.Equal(HttpStatusCode.BadRequest, noToken.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await vendor.Client.GetAsync("/api/v1/portal/session")).StatusCode);

        var request = new HttpRequestMessage(HttpMethod.Post, "/portal/auth/logout");
        request.Headers.Add("X-XSRF-TOKEN", vendor.Csrf);
        Assert.Equal(HttpStatusCode.NoContent, (await vendor.Client.SendAsync(request)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await vendor.Client.GetAsync("/api/v1/portal/session")).StatusCode);
    }

    [Fact]
    public async Task Failed_sign_ins_give_the_same_answer_whatever_the_cause()
    {
        var staff = await SignInStaff("portal-mgr4@acme.com");
        var vendor = await SignedInVendor(staff, "Portal Co 4", "ap4@portalco.example");
        var anon = _api.NewBrowser();

        var bodies = new List<string>();
        foreach (var attempt in new object[]
                 {
                     new { tenant = "acme", email = "ap4@portalco.example", password = "wrong password entirely" },
                     new { tenant = "acme", email = "ghost@portalco.example", password = Password },
                     new { tenant = "nope", email = "ap4@portalco.example", password = Password },
                     new { tenant = "beta", email = "ap4@portalco.example", password = Password },
                 })
        {
            var response = await anon.PostAsJsonAsync("/portal/auth/login", attempt);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("invalid_credentials", json.GetProperty("errorCode").GetString());
            bodies.Add($"{json.GetProperty("title")}|{json.GetProperty("detail")}|{json.GetProperty("status")}");
        }

        Assert.Single(bodies.Distinct());
        Assert.NotNull(vendor);
    }

    [Fact]
    public async Task Repeated_wrong_passwords_lock_the_account_through_the_api_too()
    {
        var staff = await SignInStaff("portal-mgr5@acme.com");
        var vendor = await SignedInVendor(staff, "Portal Co 5", "ap5@portalco.example");
        var anon = _api.NewBrowser();
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsJsonAsync("/portal/auth/login", new { tenant = "acme", email = "ap5@portalco.example", password = "wrong password " + i })).StatusCode);
        }

        var locked = await anon.PostAsJsonAsync("/portal/auth/login", new { tenant = "acme", email = "ap5@portalco.example", password = Password });
        Assert.Equal(HttpStatusCode.Unauthorized, locked.StatusCode);
        _api.Clock.Advance(TimeSpan.FromMinutes(16));
        Assert.NotNull(vendor);
    }

    [Fact]
    public async Task Portal_sign_in_endpoints_are_rate_limited()
    {
        await using var limited = _api.WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, c) =>
            c.AddInMemoryCollection(new Dictionary<string, string?> { ["RateLimiting:PortalAuthPermitLimit"] = "3" })));
        var client = limited.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost") });

        HttpResponseMessage? rejected = null;
        for (var i = 0; i < 8 && rejected is null; i++)
        {
            var r = await client.PostAsJsonAsync("/portal/auth/login", new { tenant = "acme", email = "x@y.example", password = "whatever whatever" });
            if (r.StatusCode == HttpStatusCode.TooManyRequests) rejected = r;
        }

        Assert.NotNull(rejected);
        Assert.True(rejected.Headers.Contains("Retry-After"));
    }

    [Fact]
    public async Task Only_staff_with_the_portal_permission_can_manage_vendor_users()
    {
        var plain = await SignInStaff("portal-plain@acme.com");
        Assert.Equal(HttpStatusCode.Forbidden, (await plain.Post($"/api/v1/vendors/{Guid.NewGuid()}/portal-users", new { email = "a@b.example", displayName = "A" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await plain.Client.GetAsync($"/api/v1/vendors/{Guid.NewGuid()}/portal-users")).StatusCode);

        var manager = await SignInStaff("portal-mgr6@acme.com");
        var vendorId = await CreateVendor(manager, "Portal Co 6");
        var invite = await manager.Post($"/api/v1/vendors/{vendorId}/portal-users", new { email = "ap6@portalco.example", displayName = "AP Six" });
        Assert.Equal(HttpStatusCode.OK, invite.StatusCode);
        var json = await invite.Content.ReadFromJsonAsync<JsonElement>();
        Assert.StartsWith("https://portal.test/portal/accept?tenant=acme&token=", json.GetProperty("inviteUrl").GetString());

        var dup = await manager.Post($"/api/v1/vendors/{vendorId}/portal-users", new { email = "AP6@portalco.example", displayName = "Dup" });
        Assert.Equal(HttpStatusCode.Conflict, dup.StatusCode);
    }

    [Fact]
    public async Task Invitation_pages_say_nothing_about_why_a_link_failed()
    {
        var anon = _api.NewBrowser();
        foreach (var url in new[] { "/portal/auth/invite?tenant=acme&token=nonsense", "/portal/auth/invite?tenant=nope&token=nonsense", "/portal/auth/invite?tenant=&token=", "/portal/auth/invite" })
        {
            var r = await anon.GetAsync(url);
            Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        }
    }

    // --- helpers -------------------------------------------------------------------------------------------

    private string Code(string secret) => Totp.Compute(secret, Totp.StepFor(_api.Clock.GetUtcNow()));

    private async Task<Session> SignInStaff(string email, string role = "Vendor Manager")
    {
        var client = _api.NewBrowser();
        _api.IdpA.NextUser = new FakeUser("sub-" + email, email, email.Split('@')[0]);
        await AuthenticationTests.SignInAsync(client, _api.IdpA, _api.ProviderA, "/");
        if (email.Contains("plain", StringComparison.Ordinal) == false) await _api.AssignRoleAsync(_api.TenantA, email, role, null);
        var csrf = (await client.GetFromJsonAsync<JsonElement>("/api/v1/session")).GetProperty("csrfToken").GetString()!;
        return new Session(client, csrf);
    }

    private static async Task<Guid> CreateVendor(Session s, string name)
    {
        var r = await s.Post("/api/v1/vendors", new { legalName = name, countryCode = "SA" });
        return (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static async Task<(string Email, string Token)> InviteUser(Session staff, Guid vendorId, string email)
    {
        var r = await staff.Post($"/api/v1/vendors/{vendorId}/portal-users", new { email, displayName = "AP Clerk" });
        var url = (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("inviteUrl").GetString()!;
        return (email, url[(url.IndexOf("token=", StringComparison.Ordinal) + 6)..]);
    }

    /// <summary>A vendor user with a live session, set up through the real HTTP flow.</summary>
    private async Task<Session> SignedInVendor(Session staff, string vendorName, string email)
    {
        var vendorId = await CreateVendor(staff, vendorName);
        var (_, token) = await InviteUser(staff, vendorId, email);
        var client = _api.NewBrowser();
        var accept = await client.PostAsJsonAsync("/portal/auth/invite/accept", new { tenant = "acme", token, password = Password });
        Assert.True(accept.IsSuccessStatusCode, await accept.Content.ReadAsStringAsync());
        var secret = (await accept.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("enrollment").GetProperty("secret").GetString()!;
        var enroll = await client.PostAsJsonAsync("/portal/auth/invite/enroll", new { tenant = "acme", token, code = Code(secret) });
        Assert.Equal(HttpStatusCode.OK, enroll.StatusCode);
        var csrf = (await client.GetFromJsonAsync<JsonElement>("/api/v1/portal/session")).GetProperty("csrfToken").GetString()!;
        return new Session(client, csrf);
    }

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
