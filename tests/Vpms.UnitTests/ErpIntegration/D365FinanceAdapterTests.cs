using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Vpms.ErpIntegration.Contracts;
using Vpms.ErpIntegration.Infrastructure.D365;

namespace Vpms.UnitTests.ErpIntegration;

internal static class D365Kit
{
    public static ErpContext Context(Dictionary<string, string>? sets = null, int pageSize = 200) => new(
        new ErpConnectionSettings("https://contoso.operations.dynamics.com", "tenant-1", "client-1", FakeD365Handler.Secret, sets ?? new Dictionary<string, string>(), pageSize, 30),
        new ErpCompanyMapping("USMF", "VENDINV", "STD", new Dictionary<string, string> { ["N30"] = "Net30" }, new Dictionary<string, string>(), []));

    public static D365FinanceAdapter Adapter(FakeD365Handler handler, TimeProvider? clock = null) =>
        new(new HttpClient(handler), Options.Create(new D365Options()), clock ?? new FakeTimeProvider(DateTimeOffset.Parse("2026-10-01T08:00:00Z")));
}

/// <summary>The same behaviour every adapter must have, run against the D365 adapter over the stubbed environment.</summary>
public sealed class D365AdapterContractTests : ErpAdapterContract
{
    private readonly FakeD365Handler _d365 = new();
    private readonly D365FinanceAdapter _adapter;

    public D365AdapterContractTests() => _adapter = D365Kit.Adapter(_d365);

    protected override IErpAdapter Adapter => _adapter;

    protected override void GivenVendor(string account) =>
        _d365.Add("VendorsV2", new JsonObject { ["dataAreaId"] = "USMF", ["VendorAccountNumber"] = account, ["VendorOrganizationName"] = "Vendor " + account });
}

public sealed class D365FinanceAdapterTests
{
    private readonly FakeD365Handler _d365 = new();
    private readonly ErpContext _ctx = D365Kit.Context();

    private D365FinanceAdapter Adapter() => D365Kit.Adapter(_d365);

    private static ErpInvoice Invoice(string key = "k1") =>
        new(key, "V-001", "INV-1", new DateOnly(2026, 10, 1), new DateOnly(2026, 11, 1), "SAR", 115m, 15m,
            [new ErpInvoiceLine(1, "Services", 1, 100m, 100m, "VAT15", new Dictionary<string, string> { ["CostCentre"] = "CC-1", ["Dept"] = "D-9" }), new ErpInvoiceLine(2, "More", 1, 15m, 15m, null, new Dictionary<string, string>())]);

    private void Vendor(string account = "V-001") => _d365.Add("VendorsV2", new JsonObject { ["dataAreaId"] = "USMF", ["VendorAccountNumber"] = account, ["VendorOrganizationName"] = "V" });

    [Fact]
    public async Task One_sign_in_serves_many_calls_and_the_secret_is_sent_only_to_the_sign_in_endpoint()
    {
        Vendor();
        var a = Adapter();
        await a.ListVendorsAsync(_ctx);
        await a.ListVendorsAsync(_ctx);
        await a.CreateVendorInvoiceAsync(_ctx, Invoice());

        Assert.Equal(1, _d365.TokenCalls);
        Assert.All(_d365.Requests.Where(r => !r.Url.Contains("oauth2")), r =>
        {
            Assert.DoesNotContain(FakeD365Handler.Secret, r.Url);
            Assert.DoesNotContain(FakeD365Handler.Secret, r.Body ?? "");
        });
        Assert.StartsWith("https://login.microsoftonline.com/tenant-1/", _d365.Requests[0].Url);
        Assert.Contains("scope=https%3A%2F%2Fcontoso.operations.dynamics.com%2F.default", _d365.Requests[0].Body);
    }

    [Fact]
    public async Task An_expired_token_is_renewed()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-01T08:00:00Z"));
        _d365.TokenResponse = """{"access_token":"fake-token","expires_in":600}""";
        var a = D365Kit.Adapter(_d365, clock);
        await a.ListVendorsAsync(D365Kit.Context(new Dictionary<string, string> { ["vendors"] = "VendorsV2" }, 201));
        clock.Advance(TimeSpan.FromMinutes(9)); // inside the two-minute safety margin of a ten-minute token
        await a.ListVendorsAsync(D365Kit.Context(new Dictionary<string, string> { ["vendors"] = "VendorsV2" }, 201));
        Assert.Equal(2, _d365.TokenCalls);
    }

    [Fact]
    public async Task A_refused_sign_in_is_reported_without_revealing_anything_secret()
    {
        var bad = _ctx with { Connection = _ctx.Connection with { ClientSecret = "wrong" } };
        var result = await Adapter().ValidateConnectionAsync(bad);
        Assert.Equal(ErpOutcome.Rejected, result.Outcome);
        Assert.Equal("auth_failed", result.ErrorCode);
        Assert.DoesNotContain("wrong", result.Message ?? "");
    }

    [Fact]
    public async Task The_sign_in_service_being_down_is_temporary()
    {
        _d365.TokenStatus = HttpStatusCode.ServiceUnavailable;
        var result = await Adapter().ValidateConnectionAsync(_ctx);
        Assert.Equal(ErpOutcome.Transient, result.Outcome);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, ErpOutcome.Transient, "throttled")]
    [InlineData(HttpStatusCode.ServiceUnavailable, ErpOutcome.Transient, "erp_http_503")]
    [InlineData(HttpStatusCode.InternalServerError, ErpOutcome.Transient, "erp_http_500")]
    [InlineData(HttpStatusCode.Unauthorized, ErpOutcome.Transient, "auth_failed")]
    [InlineData(HttpStatusCode.Forbidden, ErpOutcome.Transient, "auth_failed")]
    [InlineData(HttpStatusCode.BadRequest, ErpOutcome.Rejected, "erp_http_400")]
    [InlineData(HttpStatusCode.NotFound, ErpOutcome.Rejected, "erp_http_404")]
    [InlineData(HttpStatusCode.Conflict, ErpOutcome.Rejected, "erp_http_409")]
    public async Task Http_failures_are_classified_into_retry_or_refuse(HttpStatusCode status, ErpOutcome outcome, string code)
    {
        Vendor();
        _d365.Fault(_ => FakeD365Handler.Status(status, "Something went wrong"));
        var result = await Adapter().CreateVendorInvoiceAsync(_ctx, Invoice());
        Assert.Equal(outcome, result.Outcome);
        Assert.Equal(code, result.ErrorCode);
        Assert.Equal("Something went wrong", result.Message);
    }

    [Fact]
    public async Task Throttling_passes_on_the_retry_after_the_erp_asked_for()
    {
        _d365.Fault(_ => FakeD365Handler.Status(HttpStatusCode.TooManyRequests, null, TimeSpan.FromSeconds(120)));
        var result = await Adapter().ListVendorsAsync(_ctx);
        Assert.Equal(ErpOutcome.Transient, result.Outcome);
        Assert.Equal(TimeSpan.FromSeconds(120), result.RetryAfter);
    }

    [Fact]
    public async Task A_network_failure_and_a_timeout_are_temporary()
    {
        _d365.ThrowOnNext = new HttpRequestException("connection reset");
        var down = await Adapter().ValidateConnectionAsync(_ctx);
        Assert.Equal(ErpOutcome.Transient, down.Outcome);

        _d365.ThrowOnNext = null;
        await Adapter().ValidateConnectionAsync(_ctx); // signs in and caches the token
        _d365.ThrowOnNext = new TaskCanceledException("timeout");
        var slow = await Adapter().ValidateConnectionAsync(_ctx);
        Assert.Equal(ErpOutcome.Transient, slow.Outcome);
        Assert.Equal("timeout", slow.ErrorCode);
    }

    [Fact]
    public async Task An_invoice_becomes_one_journal_with_a_line_per_invoice_line_and_the_mapped_fields()
    {
        Vendor();
        var result = await Adapter().CreateVendorInvoiceAsync(_ctx, Invoice());
        Assert.Equal(ErpOutcome.Succeeded, result.Outcome);

        var header = Assert.Single(_d365.Set("VendorInvoiceJournalHeaders"));
        Assert.Equal("VENDINV", header["JournalName"]!.ToString());
        Assert.Equal("USMF", header["dataAreaId"]!.ToString());
        Assert.Equal(D365FinanceAdapter.Reference("k1"), header["Description"]!.ToString());
        Assert.True(header["Description"]!.ToString().Length <= 60);

        var lines = _d365.Set("VendorInvoiceJournalLines");
        Assert.Equal(2, lines.Count);
        Assert.All(lines, l => Assert.Equal(result.Reference, l["JournalBatchNumber"]!.ToString()));
        Assert.Equal("V-001", lines[0]["Account"]!.ToString());
        Assert.Equal("VAT15", lines[0]["ItemSalesTaxGroup"]!.ToString());
        Assert.Equal("STD", lines[0]["PostingProfile"]!.ToString());
        Assert.Equal("CC-1-D-9", lines[0]["DefaultDimensionDisplayValue"]!.ToString());
        Assert.Null(lines[1]["ItemSalesTaxGroup"]);
        Assert.Equal(100m, lines[0]["CreditAmount"]!.GetValue<decimal>());
    }

    [Fact]
    public async Task A_rejected_line_is_reported_with_the_erps_own_message()
    {
        var result = await Adapter().CreateVendorInvoiceAsync(_ctx, Invoice("k9") with { VendorAccount = "NOPE" });
        Assert.Equal(ErpOutcome.Rejected, result.Outcome);
        Assert.Contains("NOPE", result.Message);
    }

    [Fact]
    public async Task Entity_set_names_from_the_connection_replace_the_defaults()
    {
        _d365.Add("MyVendors", new JsonObject { ["dataAreaId"] = "USMF", ["VendorAccountNumber"] = "X-1", ["VendorOrganizationName"] = "Custom" });
        var ctx = D365Kit.Context(new Dictionary<string, string> { ["vendors"] = "MyVendors" });
        var vendors = await Adapter().ListVendorsAsync(ctx);
        Assert.Equal("X-1", Assert.Single(vendors.Items).Account);
        Assert.Contains(_d365.Requests, r => r.Url.Contains("/data/MyVendors?"));
        Assert.DoesNotContain(_d365.Requests, r => r.Url.Contains("/data/VendorsV2"));
    }

    [Fact]
    public async Task Listing_follows_the_next_links_to_the_end()
    {
        for (var i = 1; i <= 7; i++) Vendor($"V-{i:000}");
        _d365.PageSize = 3;
        var vendors = await Adapter().ListVendorsAsync(_ctx);
        Assert.Equal(7, vendors.Items.Count);
        Assert.Equal(7, vendors.Items.Select(v => v.Account).Distinct().Count());
        Assert.True(_d365.Requests.Count(r => r.Url.Contains("/data/VendorsV2")) >= 3);
    }

    [Fact]
    public async Task A_failure_on_a_later_page_fails_the_whole_listing_instead_of_returning_part_of_it()
    {
        for (var i = 1; i <= 6; i++) Vendor($"V-{i:000}");
        _d365.PageSize = 3;
        var a = Adapter();
        await a.ListVendorsAsync(_ctx); // signs in, so only data requests are counted below
        _d365.FaultOnRequest(2, _ => FakeD365Handler.Status(HttpStatusCode.ServiceUnavailable));
        var failed = await a.ListVendorsAsync(_ctx);
        Assert.Equal(ErpOutcome.Transient, failed.Outcome);
        Assert.Empty(failed.Items);
    }

    [Fact]
    public async Task A_vendor_is_created_once_then_updated_through_its_key()
    {
        var vendor = new ErpVendor(Guid.NewGuid(), "Acme Ltd", "300000000000003", "SA", "SAR", "N30", null);
        var created = await Adapter().UpsertVendorAsync(_ctx, vendor, "key-v1");
        Assert.Equal(ErpOutcome.Succeeded, created.Outcome);
        var row = Assert.Single(_d365.Set("VendorsV2"));
        Assert.Equal("Net30", row["PaymentTermsName"]!.ToString());
        Assert.Equal("300000000000003", row["TaxRegistrationNumber"]!.ToString());

        var updated = await Adapter().UpsertVendorAsync(_ctx, vendor with { Name = "Acme Limited", ExistingAccount = created.Reference }, "key-v2");
        Assert.Equal(created.Reference, updated.Reference);
        Assert.Single(_d365.Set("VendorsV2"));
        Assert.Equal("Acme Limited", _d365.Set("VendorsV2")[0]["VendorOrganizationName"]!.ToString());
        Assert.Contains(_d365.Requests, r => r.Method == HttpMethod.Patch);
    }

    [Fact]
    public async Task Quotes_in_values_cannot_break_out_of_a_filter()
    {
        Vendor();
        var invoice = Invoice("k'1") with { InvoiceNumber = "A'B" };
        var result = await Adapter().CreateVendorInvoiceAsync(_ctx, invoice);
        Assert.Equal(ErpOutcome.Succeeded, result.Outcome);
        var ctx = D365Kit.Context(new Dictionary<string, string>());
        var q = await Adapter().GetDimensionValuesAsync(ctx, "Cost'Centre");
        Assert.True(q.IsSuccess);
        Assert.Contains(_d365.Requests, r => r.Url.Contains("Cost''Centre") || r.Url.Contains("Cost%27%27Centre"));
    }

    [Fact]
    public async Task Listed_invoices_carry_the_reference_reconciliation_matches_on()
    {
        Vendor();
        await Adapter().CreateVendorInvoiceAsync(_ctx, Invoice("key-A"));
        var listed = await Adapter().ListInvoicesAsync(_ctx, null);
        var doc = Assert.Single(listed.Items);
        Assert.Equal(Adapter().DocumentReference("key-A"), doc.DocumentKey);
        Assert.Equal(115m, doc.Total);
        Assert.Equal("V-001", doc.VendorAccount);
        Assert.Equal(ErpDocumentState.Posted, doc.State);
        Assert.Equal(new DateOnly(2026, 10, 1), doc.InvoiceDate);
    }

    [Fact]
    public async Task An_attachment_is_sent_as_base64_and_a_repeat_finds_it()
    {
        Vendor();
        var created = await Adapter().CreateVendorInvoiceAsync(_ctx, Invoice());
        var file = new ErpAttachment("inv.pdf", "application/pdf", [1, 2, 3, 4]);
        await Adapter().AttachDocumentAsync(_ctx, created.Reference!, file, "att-1");
        var row = Assert.Single(_d365.Set("DocumentAttachments"));
        Assert.Equal(Convert.ToBase64String([1, 2, 3, 4]), row["FileContent"]!.ToString());
        Assert.Equal(D365FinanceAdapter.Reference("att-1"), row["Name"]!.ToString());
        Assert.Equal(ErpOutcome.AlreadyExists, (await Adapter().AttachDocumentAsync(_ctx, created.Reference!, file, "att-1")).Outcome);
        Assert.Single(_d365.Set("DocumentAttachments"));
    }

    [Fact]
    public async Task Purchase_orders_are_assembled_from_headers_and_lines()
    {
        _d365.Add("PurchaseOrderHeadersV2", new JsonObject { ["dataAreaId"] = "USMF", ["PurchaseOrderNumber"] = "PO-1", ["OrderVendorAccountNumber"] = "V-001", ["CurrencyCode"] = "SAR", ["PurchaseOrderStatus"] = "Confirmed", ["OrderCreationDate"] = "2026-09-01T00:00:00Z" });
        _d365.Add("PurchaseOrderLinesV2", new JsonObject { ["dataAreaId"] = "USMF", ["PurchaseOrderNumber"] = "PO-1", ["LineNumber"] = 1, ["LineDescription"] = "Chairs", ["OrderedPurchaseQuantity"] = 10, ["PurchasePrice"] = 50, ["ReceivedPurchaseQuantity"] = 4 });
        var orders = await Adapter().ListPurchaseOrdersAsync(_ctx, null);
        var po = Assert.Single(orders.Items);
        Assert.Equal("PO-1", po.PoNumber);
        Assert.Equal(new DateOnly(2026, 9, 1), po.OrderDate);
        var line = Assert.Single(po.Lines);
        Assert.Equal(10m, line.OrderedQuantity);
        Assert.Equal(4m, line.InvoicedQuantity);
    }
}
