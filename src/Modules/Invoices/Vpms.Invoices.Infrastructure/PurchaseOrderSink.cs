using Vpms.Invoices.Application;
using Vpms.Invoices.Contracts;

namespace Vpms.Invoices.Infrastructure;

public sealed class PurchaseOrderSink(IPurchaseOrderService orders) : IPurchaseOrderSink
{
    public async Task<PoSinkResult> ImportAsync(Guid legalEntityId, IReadOnlyList<PoSinkItem> items, CancellationToken ct = default)
    {
        var result = await orders.ImportAsync(legalEntityId, items.Select(i => new PoImportItem(
            i.PoNumber, i.Version, null, i.ErpVendorAccount, i.CurrencyCode, i.Status, i.OrderDate, i.InvoicedValue,
            i.Lines.Select(l => new PoLineInput(l.LineNo, l.Description, l.OrderedQuantity, l.UnitPrice, l.InvoicedQuantity)).ToList())).ToList(), ct);
        return new PoSinkResult(result.Created, result.Updated, result.Unchanged, result.Errors.Select(e => new PoSinkError(e.PoNumber, e.Code, e.Message)).ToList());
    }
}
