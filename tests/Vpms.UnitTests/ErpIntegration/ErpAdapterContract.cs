using Vpms.ErpIntegration.Contracts;

namespace Vpms.UnitTests.ErpIntegration;

/// <summary>
/// What every <see cref="IErpAdapter"/> must do, whatever the ERP behind it. The mock and the Dynamics 365 adapter (against a stubbed
/// server) both run this suite, so the queue and reconciliation can rely on the same behaviour from either.
/// </summary>
public abstract class ErpAdapterContract
{
    protected abstract IErpAdapter Adapter { get; }

    protected static readonly ErpContext Context = new(
        new ErpConnectionSettings("https://erp.example.test", "tenant-id", "client-id", "secret", new Dictionary<string, string>(), 200, 60),
        new ErpCompanyMapping("USMF", "VENDINV", null, new Dictionary<string, string>(), new Dictionary<string, string>(), []));

    protected static ErpInvoice Invoice(string key = "t:e:inv:1:1", decimal total = 115m, string number = "INV-1") =>
        new(key, "V-001", number, new DateOnly(2026, 10, 1), new DateOnly(2026, 11, 1), "SAR", total, 15m,
            [new ErpInvoiceLine(1, "Services", 1, 100m, 100m, "VAT15", new Dictionary<string, string>())]);

    /// <summary>Makes the ERP aware of the vendor so invoices for it are accepted.</summary>
    protected abstract void GivenVendor(string account);

    [Fact]
    public async Task Creating_an_invoice_twice_with_the_same_key_creates_one_document()
    {
        GivenVendor("V-001");
        var first = await Adapter.CreateVendorInvoiceAsync(Context, Invoice());
        var second = await Adapter.CreateVendorInvoiceAsync(Context, Invoice());

        Assert.Equal(ErpOutcome.Succeeded, first.Outcome);
        Assert.Equal(ErpOutcome.AlreadyExists, second.Outcome);
        Assert.Equal(first.Reference, second.Reference);
        var listed = await Adapter.ListInvoicesAsync(Context, null);
        Assert.Single(listed.Items, i => i.DocumentKey == "t:e:inv:1:1");
    }

    [Fact]
    public async Task Different_keys_create_different_documents()
    {
        GivenVendor("V-001");
        var a = await Adapter.CreateVendorInvoiceAsync(Context, Invoice("k1", number: "INV-1"));
        var b = await Adapter.CreateVendorInvoiceAsync(Context, Invoice("k2", number: "INV-2"));
        Assert.NotEqual(a.Reference, b.Reference);
        Assert.Equal(2, (await Adapter.ListInvoicesAsync(Context, null)).Items.Count);
    }

    [Fact]
    public async Task An_unknown_vendor_is_a_permanent_rejection_not_a_retry()
    {
        var result = await Adapter.CreateVendorInvoiceAsync(Context, Invoice() with { VendorAccount = "NOPE" });
        Assert.Equal(ErpOutcome.Rejected, result.Outcome);
        Assert.False(string.IsNullOrEmpty(result.ErrorCode));
        Assert.Null(result.Reference);
    }

    [Fact]
    public async Task Upserting_a_vendor_with_the_same_key_returns_the_same_account()
    {
        var vendor = new ErpVendor(Guid.NewGuid(), "Acme Ltd", "300000000000003", "SA", "SAR", null, null);
        var first = await Adapter.UpsertVendorAsync(Context, vendor, "t:e:vendor:1:1");
        var second = await Adapter.UpsertVendorAsync(Context, vendor, "t:e:vendor:1:1");
        Assert.True(first.IsSuccess);
        Assert.Equal(first.Reference, second.Reference);
        Assert.Single((await Adapter.ListVendorsAsync(Context)).Items, v => v.Account == first.Reference);
    }

    [Fact]
    public async Task Attaching_a_document_twice_is_a_no_op_the_second_time()
    {
        GivenVendor("V-001");
        var inv = await Adapter.CreateVendorInvoiceAsync(Context, Invoice());
        var file = new ErpAttachment("inv.pdf", "application/pdf", [1, 2, 3]);
        var first = await Adapter.AttachDocumentAsync(Context, inv.Reference!, file, "att-1");
        var second = await Adapter.AttachDocumentAsync(Context, inv.Reference!, file, "att-1");
        Assert.Equal(ErpOutcome.Succeeded, first.Outcome);
        Assert.Equal(ErpOutcome.AlreadyExists, second.Outcome);
    }

    [Fact]
    public async Task The_connection_check_succeeds_for_a_working_setup()
    {
        Assert.True((await Adapter.ValidateConnectionAsync(Context)).IsSuccess);
    }
}
