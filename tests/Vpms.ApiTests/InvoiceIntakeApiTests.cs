using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Vpms.BuildingBlocks.Security;
using Vpms.Documents.Contracts;
using Vpms.Identity.Contracts;
using Vpms.Invoices.Application;
using Vpms.Invoices.Domain;
using Vpms.Invoices.Infrastructure;
using Vpms.TestSupport;

namespace Vpms.ApiTests;

[Collection(SqlServerCollection.Name)]
public sealed class InvoiceIntakeApiTests : IClassFixture<ApiFixtureHolder>
{
    private const string Password = "purple elephants dance quietly";
    private static readonly byte[] Pdf = Encoding.ASCII.GetBytes("%PDF-1.7\n%%EOF");
    private static readonly Guid Maker = Guid.NewGuid();
    private static readonly Guid Checker = Guid.NewGuid();
    private readonly ApiFixture _api;

    public InvoiceIntakeApiTests(ApiFixtureHolder holder) => _api = holder.Api;

    [Fact]
    public async Task A_document_upload_makes_a_draft_and_the_extractor_leaves_a_suggestion_record()
    {
        var (entity, vendor) = await Master("IN1");
        var clerk = await SignIn("in-clerk1@acme.com", "Invoice Clerk");

        var response = await clerk.Upload("/api/v1/invoices/from-document", Form(entity, vendor, "scan.pdf", Pdf));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var draft = await response.Content.ReadFromJsonAsync<JsonElement>();
        var id = draft.GetProperty("id").GetGuid();
        Assert.Equal("Document", draft.GetProperty("source").GetString());
        Assert.Equal("Draft", draft.GetProperty("status").GetString());

        Assert.Equal("incomplete_invoice", await ErrorCode(await clerk.Post($"/api/v1/invoices/{id}/submit", new { })));
        Assert.Empty((await clerk.Client.GetFromJsonAsync<JsonElement>($"/api/v1/invoices/{id}/extractions")).EnumerateArray());

        await _api.ScanAllAsync();
        await ActiveVendorKit.AsAsync(_api.Services, _api.TenantA, Maker, s => s.GetRequiredService<IInvoiceExtractionService>().RunPendingAsync());
        var suggestions = await clerk.Client.GetFromJsonAsync<JsonElement>($"/api/v1/invoices/{id}/extractions");
        var one = Assert.Single(suggestions.EnumerateArray());
        Assert.Equal("none", one.GetProperty("engine").GetString());
        Assert.Equal("Suggested", one.GetProperty("status").GetString());

        Assert.Equal(HttpStatusCode.NoContent, (await clerk.Post($"/api/v1/invoices/{id}/extractions/{one.GetProperty("id").GetGuid()}/dismiss", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await clerk.Post($"/api/v1/invoices/{id}/extractions/{Guid.NewGuid()}/dismiss", new { })).StatusCode);
    }

    [Fact]
    public async Task Document_upload_needs_the_create_permission_for_that_entity()
    {
        var (entityX, vendorX) = await Master("IN2X");
        var (entityY, vendorY) = await Master("IN2Y");
        var onlyX = await SignIn("in-onlyx@acme.com");
        await _api.CreateRoleAndAssign(_api.TenantA, "in-onlyx@acme.com", "Entity X Doc Clerk", [Permissions.InvoiceView, Permissions.InvoiceCreate, Permissions.InvoiceEdit], entityX);

        Assert.Equal(HttpStatusCode.Created, (await onlyX.Upload("/api/v1/invoices/from-document", Form(entityX, vendorX, "a.pdf", Pdf))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await onlyX.Upload("/api/v1/invoices/from-document", Form(entityY, vendorY, "a.pdf", Pdf))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await onlyX.Upload("/api/v1/invoices/from-document", Form(entityX, vendorX, "fake.pdf", Encoding.ASCII.GetBytes("MZ no")))).StatusCode);
    }

    [Fact]
    public async Task The_unmatched_mail_queue_and_mailbox_settings_are_entity_scoped_and_permission_gated()
    {
        var (entityX, vendorX) = await Master("IN3X");
        var (entityY, _) = await Master("IN3Y");
        var plain = await SignIn("in-plain3@acme.com");
        Assert.Equal(HttpStatusCode.Forbidden, (await plain.Client.GetAsync("/api/v1/invoice-intake/messages")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await plain.Client.GetAsync($"/api/v1/admin/legal-entities/{entityX}/invoice-mailbox")).StatusCode);

        var manager = await SignIn("in-mgr3@acme.com");
        await _api.CreateRoleAndAssign(_api.TenantA, "in-mgr3@acme.com", "Entity X Intake", [Permissions.InvoiceIntakeManage, Permissions.EntityView], entityX);

        var mailbox = $"in3x-{Guid.NewGuid():N}@acme.example";
        var saved = await manager.Put($"/api/v1/admin/legal-entities/{entityX}/invoice-mailbox", new { address = mailbox, enabled = true });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal(mailbox, (await manager.Client.GetFromJsonAsync<JsonElement>($"/api/v1/admin/legal-entities/{entityX}/invoice-mailbox")).GetProperty("address").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.Put($"/api/v1/admin/legal-entities/{entityY}/invoice-mailbox", new { address = "other@acme.example", enabled = true })).StatusCode);
        Assert.Equal("invalid_email", await ErrorCode(await manager.Put($"/api/v1/admin/legal-entities/{entityX}/invoice-mailbox", new { address = "nope", enabled = true })));

        var onX = await SeedUnmatched(entityX, "x-sender@nowhere.example");
        var onY = await SeedUnmatched(entityY, "y-sender@nowhere.example");

        var queue = await manager.Client.GetFromJsonAsync<JsonElement>("/api/v1/invoice-intake/messages?status=Unmatched");
        var ids = queue.EnumerateArray().Select(m => m.GetProperty("id").GetGuid()).ToList();
        Assert.Contains(onX, ids);
        Assert.DoesNotContain(onY, ids);

        Assert.Equal(HttpStatusCode.NotFound, (await manager.Post($"/api/v1/invoice-intake/messages/{onY}/assign", new { vendorId = vendorX })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await manager.Post($"/api/v1/invoice-intake/messages/{onY}/dismiss", new { reason = "x" })).StatusCode);

        var assigned = await manager.Post($"/api/v1/invoice-intake/messages/{onX}/assign", new { vendorId = vendorX });
        Assert.Equal(HttpStatusCode.OK, assigned.StatusCode);
        var body = await assigned.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Processed", body.GetProperty("status").GetString());
        Assert.NotEqual(Guid.Empty, body.GetProperty("invoiceId").GetGuid());
        Assert.Equal(HttpStatusCode.Conflict, (await manager.Post($"/api/v1/invoice-intake/messages/{onX}/assign", new { vendorId = vendorX })).StatusCode);
    }

    [Fact]
    public async Task A_vendor_submits_and_follows_invoices_in_the_portal_without_seeing_other_vendors_or_staff_areas()
    {
        var (entity, vendorA) = await Master("IN4");
        var vendorB = await ActiveVendorKit.ActiveVendorAsync(_api.Services, _api.TenantA, entity, "Vendor IN4B", Maker, Checker);
        var staff = await SignIn("in-mgr4@acme.com", "Vendor Manager");
        var a = await SignedInVendor(staff, vendorA, "in4a@portal.example");
        var b = await SignedInVendor(staff, vendorB, "in4b@portal.example");

        var form = new MultipartFormDataContent
        {
            { new StringContent(entity.ToString()), "legalEntityId" }, { new StringContent("PORTAL-100"), "invoiceNumber" }, { new StringContent(TestDates.Iso(TestDates.Ago(5))), "invoiceDate" },
            { new StringContent(TestDates.Iso(TestDates.Ago(5).AddDays(30))), "dueDate" }, { new StringContent("SAR"), "currencyCode" }, { new StringContent("100"), "subtotal" },
            { new StringContent("15"), "taxTotal" }, { new StringContent("115"), "total" }, { new StringContent("PO-77"), "poReference" },
            { new ByteArrayContent(Pdf), "file", "invoice.pdf" },
        };
        var submitted = await a.Upload("/api/v1/portal/invoices", form);
        Assert.Equal(HttpStatusCode.OK, submitted.StatusCode);
        var invoice = await submitted.Content.ReadFromJsonAsync<JsonElement>();
        var id = invoice.GetProperty("id").GetGuid();
        // PO-77 is not a known purchase order, so the invoice needs attention; the vendor is told that much and no more
        Assert.Equal("NeedsAttention", invoice.GetProperty("status").GetString());
        Assert.False(invoice.TryGetProperty("statusReason", out _));
        Assert.DoesNotContain("po_not_found", await submitted.Content.ReadAsStringAsync());

        Assert.Single((await a.Client.GetFromJsonAsync<JsonElement>("/api/v1/portal/invoices")).EnumerateArray());
        Assert.Empty((await b.Client.GetFromJsonAsync<JsonElement>("/api/v1/portal/invoices")).EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await b.Client.GetAsync($"/api/v1/portal/invoices/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await a.Client.GetAsync($"/api/v1/portal/invoices/{id}")).StatusCode);

        // the staff side sees it as a portal invoice; the vendor cookie opens no staff door and the reverse
        var clerk = await SignIn("in-clerk4@acme.com", "Invoice Clerk");
        var list = await clerk.Client.GetFromJsonAsync<JsonElement>("/api/v1/invoices?source=Portal&search=PORTAL-100");
        Assert.Single(list.EnumerateArray());
        Assert.Equal(HttpStatusCode.Unauthorized, (await a.Client.GetAsync("/api/v1/invoices")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await clerk.Client.GetAsync("/api/v1/portal/invoices")).StatusCode);

        // missing essentials are refused with a stable code
        var bad = new MultipartFormDataContent
        {
            { new StringContent(entity.ToString()), "legalEntityId" }, { new StringContent("PORTAL-101"), "invoiceNumber" }, { new StringContent(TestDates.Iso(TestDates.Ago(5))), "invoiceDate" },
            { new StringContent("SAR"), "currencyCode" }, { new StringContent("0"), "subtotal" }, { new StringContent("0"), "taxTotal" }, { new StringContent("0"), "total" },
            { new ByteArrayContent(Pdf), "file", "invoice.pdf" },
        };
        Assert.Equal("incomplete_invoice", await ErrorCode(await a.Upload("/api/v1/portal/invoices", bad)));
    }

    // --- helpers -------------------------------------------------------------------------------------------

    private async Task<(Guid Entity, Guid Vendor)> Master(string code)
    {
        var entity = await ActiveVendorKit.CreateEntityAsync(_api.Services, _api.TenantA, Maker, code);
        var vendor = await ActiveVendorKit.ActiveVendorAsync(_api.Services, _api.TenantA, entity, "Vendor " + code, Maker, Checker);
        return (entity, vendor);
    }

    private static MultipartFormDataContent Form(Guid entity, Guid vendor, string name, byte[] bytes) => new()
    {
        { new StringContent(entity.ToString()), "legalEntityId" }, { new StringContent(vendor.ToString()), "vendorId" }, { new ByteArrayContent(bytes), "file", name },
    };

    /// <summary>An unmatched email with one stored attachment, as the worker would leave it.</summary>
    private async Task<Guid> SeedUnmatched(Guid entity, string from)
    {
        var messageId = Guid.NewGuid();
        return await ActiveVendorKit.AsAsync(_api.Services, _api.TenantA, Maker, async s =>
        {
            var info = await s.GetRequiredService<IAttachmentService>().UploadAsync(
                new UploadRequest("IntakeMessage", messageId.ToString(), entity, "invoice.pdf", new MemoryStream(Pdf), SystemActors.Intake, UploaderKind.System));
            var db = s.GetRequiredService<InvoicesDbContext>();
            db.IntakeMessages.Add(new IntakeMessage
            {
                Id = messageId, LegalEntityId = entity, ExternalMessageId = "seed-" + messageId, FromAddress = from, Subject = "Invoice", ReceivedAt = DateTimeOffset.UtcNow,
                Status = IntakeStatus.Unmatched, Attempts = 1, AttachmentIds = info.Id.ToString(), CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
            return messageId;
        });
    }

    private string Code(string secret) => Totp.Compute(secret, Totp.StepFor(_api.Clock.GetUtcNow()));

    private async Task<Session> SignedInVendor(Session staff, Guid vendorId, string email)
    {
        var invite = await staff.Post($"/api/v1/vendors/{vendorId}/portal-users", new { email, displayName = "AP Clerk" });
        var url = (await invite.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("inviteUrl").GetString()!;
        var token = url[(url.IndexOf("token=", StringComparison.Ordinal) + 6)..];
        var client = _api.NewBrowser();
        var accept = await client.PostAsJsonAsync("/portal/auth/invite/accept", new { tenant = "acme", token, password = Password });
        var secret = (await accept.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("enrollment").GetProperty("secret").GetString()!;
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/portal/auth/invite/enroll", new { tenant = "acme", token, code = Code(secret) })).StatusCode);
        var csrf = (await client.GetFromJsonAsync<JsonElement>("/api/v1/portal/session")).GetProperty("csrfToken").GetString()!;
        return new Session(client, csrf);
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
        private Task<HttpResponseMessage> Send(HttpMethod m, string url, HttpContent content)
        {
            var request = new HttpRequestMessage(m, url) { Content = content };
            request.Headers.Add("X-XSRF-TOKEN", Csrf);
            return Client.SendAsync(request);
        }

        public Task<HttpResponseMessage> Post(string url, object body) => Send(HttpMethod.Post, url, JsonContent.Create(body));

        public Task<HttpResponseMessage> Put(string url, object body) => Send(HttpMethod.Put, url, JsonContent.Create(body));

        public Task<HttpResponseMessage> Upload(string url, MultipartFormDataContent form) => Send(HttpMethod.Post, url, form);
    }
}
