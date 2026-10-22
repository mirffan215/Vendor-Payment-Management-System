using Vpms.ErpIntegration.Contracts;
using Vpms.ErpIntegration.Infrastructure;

namespace Vpms.UnitTests.ErpIntegration;

public sealed class MockErpAdapterContractTests : ErpAdapterContract
{
    private readonly MockErpAdapter _mock = new();

    protected override IErpAdapter Adapter => _mock;

    protected override void GivenVendor(string account) => _mock.AddVendor(account, "Vendor " + account);
}

public sealed class MockErpAdapterBehaviourTests
{
    private static readonly ErpContext Ctx = new(
        new ErpConnectionSettings("https://erp.example.test", "t", "c", "s", new Dictionary<string, string>(), 200, 60),
        new ErpCompanyMapping("USMF", "J", null, new Dictionary<string, string>(), new Dictionary<string, string>(), []));

    private static ErpInvoice Invoice() =>
        new("k1", "V-1", "INV-1", new DateOnly(2026, 10, 1), null, "SAR", 100m, 0m, [new ErpInvoiceLine(1, "x", 1, 100m, 100m, null, new Dictionary<string, string>())]);

    [Fact]
    public async Task Scripted_failures_are_returned_in_order_and_then_the_mock_recovers()
    {
        var mock = new MockErpAdapter();
        mock.AddVendor("V-1", "V");
        mock.FailTimes("CreateVendorInvoice", 2, ErpResult.Transient("erp_unavailable"));

        Assert.Equal(ErpOutcome.Transient, (await mock.CreateVendorInvoiceAsync(Ctx, Invoice())).Outcome);
        Assert.Equal(ErpOutcome.Transient, (await mock.CreateVendorInvoiceAsync(Ctx, Invoice())).Outcome);
        Assert.Equal(ErpOutcome.Succeeded, (await mock.CreateVendorInvoiceAsync(Ctx, Invoice())).Outcome);
        Assert.Equal(3, mock.CountCalls("CreateVendorInvoice"));
        Assert.Single(mock.Invoices);
    }

    [Fact]
    public async Task Planted_changes_show_up_in_the_listing()
    {
        var mock = new MockErpAdapter();
        mock.AddVendor("V-1", "V");
        await mock.CreateVendorInvoiceAsync(Ctx, Invoice());
        mock.ChangeInvoice("k1", total: 250m);
        var unknown = mock.AddUnknownInvoice("V-1", "STRAY-1", 40m);

        var listed = (await mock.ListInvoicesAsync(Ctx, null)).Items;
        Assert.Equal(250m, listed.Single(i => i.DocumentKey == "k1").Total);
        Assert.Contains(listed, i => i.Reference == unknown && i.InvoiceNumber == "STRAY-1");

        mock.RemoveInvoice("k1");
        Assert.DoesNotContain((await mock.ListInvoicesAsync(Ctx, null)).Items, i => i.DocumentKey == "k1");
    }
}
