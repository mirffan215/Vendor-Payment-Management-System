namespace Vpms.Invoices.Contracts;

public sealed record PoSinkLine(string LineNo, string Description, decimal OrderedQuantity, decimal UnitPrice, decimal InvoicedQuantity);

public sealed record PoSinkItem(
    string PoNumber, int Version, string ErpVendorAccount, string CurrencyCode, string Status, DateOnly OrderDate, decimal InvoicedValue, IReadOnlyList<PoSinkLine> Lines);

public sealed record PoSinkError(string PoNumber, string Code, string Message);

public sealed record PoSinkResult(int Created, int Updated, int Unchanged, IReadOnlyList<PoSinkError> Errors);

/// <summary>Where purchase orders from the ERP land. Importing is idempotent: the same version with the same content changes nothing.</summary>
public interface IPurchaseOrderSink
{
    Task<PoSinkResult> ImportAsync(Guid legalEntityId, IReadOnlyList<PoSinkItem> items, CancellationToken ct = default);
}
