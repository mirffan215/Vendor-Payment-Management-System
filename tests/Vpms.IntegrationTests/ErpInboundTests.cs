using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vpms.ErpIntegration.Application;
using Vpms.ErpIntegration.Contracts;
using Vpms.ErpIntegration.Domain;
using Vpms.ErpIntegration.Infrastructure;
using Vpms.Invoices.Application;
using Vpms.TestSupport;
using Vpms.Vendors.Contracts;

namespace Vpms.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class ErpInboundTests(SqlServerFixture sql) : ErpScenarioBase(sql)
{
    private Task<InboundResult> Pull() => W.AsAsync(W.TenantA, W.Admin, s => s.GetRequiredService<IErpInbound>().PullAsync());

    private Task<List<ErpInboxMessage>> Inbox(string? kind = null) =>
        W.AsAsync(W.TenantA, W.Admin, s => s.GetRequiredService<ErpDbContext>().Inbox.AsNoTracking().Where(m => kind == null || m.Kind == kind).OrderBy(m => m.ReceivedAt).ToListAsync());

    private Task<IReadOnlyList<PurchaseOrderDto>> Orders() =>
        W.AsAsync(W.TenantA, W.Admin, s => s.GetRequiredService<IPurchaseOrderService>().ListAsync(Entity, null, null, 0, 50));

    private async Task LinkVendor(string account = "V-ERP")
    {
        Erp.AddVendor(account, "Al Amal Trading");
        await W.AsAsync(W.TenantA, W.Admin, async s =>
        {
            await s.GetRequiredService<IVendorErpSource>().LinkErpAccountAsync(Vendor, Entity, account);
            return true;
        });
    }

    private static ErpPurchaseOrder Po(string number = "PO-1", decimal price = 50m, int version = 1) =>
        new(number, version, "V-ERP", "SAR", "Open", new DateOnly(2026, 9, 1), 0m, [new ErpPurchaseOrderLine("1", "Chairs", 10, price, 0)]);

    [Fact]
    public async Task Purchase_orders_from_the_erp_reach_the_verification_data_once()
    {
        await ConnectErpAsync();
        await LinkVendor();
        Erp.AddPurchaseOrder(Po());

        var first = await Pull();
        Assert.Equal(1, first.Applied);
        var order = Assert.Single(await Orders());
        Assert.Equal("PO-1", order.PoNumber);
        Assert.Equal(Vendor, order.VendorId);
        Assert.Equal(50m, order.Lines.Single().UnitPrice);

        var again = await Pull();
        Assert.Equal(0, again.Received);
        Assert.Equal(1, again.Duplicates);
        Assert.Single(await Inbox(InboundKind.PurchaseOrder));
        Assert.Single(await Orders());
    }

    [Fact]
    public async Task A_changed_purchase_order_replaces_the_old_one_and_keeps_both_messages()
    {
        await ConnectErpAsync();
        await LinkVendor();
        Erp.AddPurchaseOrder(Po());
        await Pull();
        Erp.AddPurchaseOrder(Po(price: 55m));
        var second = await Pull();

        Assert.Equal(1, second.Applied);
        var order = Assert.Single(await Orders());
        Assert.Equal(55m, order.Lines.Single().UnitPrice);
        Assert.Equal(2, order.Version);
        var messages = await Inbox(InboundKind.PurchaseOrder);
        Assert.Equal([1, 2], messages.Select(m => m.Sequence).ToArray());
    }

    [Fact]
    public async Task A_purchase_order_for_a_vendor_not_yet_known_waits_and_is_applied_once_the_vendor_is_linked()
    {
        await ConnectErpAsync();
        Erp.AddPurchaseOrder(Po());

        var first = await Pull();
        Assert.Equal(1, first.Failed);
        Assert.Empty(await Orders());
        var failed = Assert.Single(await Inbox(InboundKind.PurchaseOrder));
        Assert.Equal(InboxStatus.Failed, failed.Status);
        Assert.StartsWith("unknown_vendor", failed.Error);

        await LinkVendor();
        var retry = await Pull();
        Assert.Equal(0, retry.Failed);
        Assert.Single(await Orders());
        Assert.Equal(InboxStatus.Applied, Assert.Single(await Inbox(InboundKind.PurchaseOrder)).Status);
    }

    [Fact]
    public async Task What_the_erp_says_about_a_posted_invoice_is_recorded_without_touching_the_invoice()
    {
        var invoice = await ApprovedInvoice();
        await ConnectErpAsync();
        await SettleAsync();
        var post = (await Records(SyncType.PostInvoice)).Single();

        Assert.Null(await W.AsAsync(W.TenantA, W.Admin, s => s.GetRequiredService<IErpInboundQuery>().InvoiceStatusAsync(invoice.Id)));
        await Pull();
        var posted = await W.AsAsync(W.TenantA, W.Admin, s => s.GetRequiredService<IErpInboundQuery>().InvoiceStatusAsync(invoice.Id));
        Assert.Equal(ErpDocumentState.Posted, posted!.State);
        Assert.Equal(post.ErpReference, posted.Reference);

        var paidAt = Clock.GetUtcNow();
        Erp.ChangeInvoice(post.IdempotencyKey, state: ErpDocumentState.Paid, paidAt: paidAt);
        await Pull();
        var paid = await W.AsAsync(W.TenantA, W.Admin, s => s.GetRequiredService<IErpInboundQuery>().InvoiceStatusAsync(invoice.Id));
        Assert.Equal(ErpDocumentState.Paid, paid!.State);
        Assert.Equal(paidAt, paid.PaidAt);

        // the approved invoice itself is unchanged
        var after = await Invoice(invoice.Id);
        Assert.Equal(invoice.Status, after.Status);
        Assert.Equal(invoice.Version, after.Version);
        Assert.Equal(invoice.Total, after.Total);
    }

    [Fact]
    public async Task Documents_the_erp_holds_that_vpms_did_not_post_are_not_taken_as_vpms_invoices()
    {
        await ApprovedInvoice();
        await ConnectErpAsync();
        await SettleAsync();
        Erp.AddUnknownInvoice(Erp.Vendors.Single().Account, "STRAY-1", 40m);
        await Pull();
        Assert.Single(await Inbox(InboundKind.InvoiceStatus));
    }

    [Fact]
    public async Task A_vendor_payment_is_matched_to_the_invoice_it_settles()
    {
        var invoice = await ApprovedInvoice();
        await ConnectErpAsync();
        await SettleAsync();
        var post = (await Records(SyncType.PostInvoice)).Single();
        Erp.AddPayment(new ErpPayment("PAY-1", Erp.Vendors.Single().Account, post.ErpReference, 115m, "SAR", Clock.GetUtcNow()));
        Erp.AddPayment(new ErpPayment("PAY-2", Erp.Vendors.Single().Account, "UNKNOWN-DOC", 10m, "SAR", Clock.GetUtcNow()));

        await Pull();
        var payments = await Inbox(InboundKind.Payment);
        Assert.Equal(2, payments.Count);
        Assert.Contains(invoice.Id.ToString().ToLowerInvariant(), payments.Single(p => p.ExternalId == "PAY-1").PayloadJson.ToLowerInvariant());
        Assert.Contains("\"invoiceId\":null", payments.Single(p => p.ExternalId == "PAY-2").PayloadJson);
        Assert.All(payments, p => Assert.Equal(InboxStatus.Applied, p.Status));
    }

    [Fact]
    public async Task A_failing_pull_is_reported_and_does_not_stop_the_other_kinds()
    {
        await ConnectErpAsync();
        await LinkVendor();
        Erp.AddPurchaseOrder(Po());
        Erp.FailTimes("ListPurchaseOrders", 1, ErpResult.Transient("erp_unavailable"));

        var run = await Pull();
        Assert.Contains(run.PullErrors, e => e.EndsWith("PurchaseOrder: erp_unavailable"));
        Assert.Empty(await Orders());

        var next = await Pull();
        Assert.Empty(next.PullErrors);
        Assert.Single(await Orders());
    }

    [Fact]
    public async Task Nothing_is_pulled_without_a_connection_and_other_tenants_see_nothing()
    {
        Erp.AddPurchaseOrder(Po());
        Assert.Equal(0, (await Pull()).Received);

        var invoice = await ApprovedInvoice();
        await ConnectErpAsync();
        await SettleAsync();
        await Pull();
        var userB = await W.UserAsync(W.TenantB, "inb@t.com", null, "ERP Operator");
        Assert.Null(await W.AsAsync(W.TenantB, userB, s => s.GetRequiredService<IErpInboundQuery>().InvoiceStatusAsync(invoice.Id)));
        Assert.Equal(0, await W.AsAsync(W.TenantB, W.Admin, async s => (await s.GetRequiredService<ErpDbContext>().Inbox.CountAsync())));
    }
}
