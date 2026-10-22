namespace Vpms.Invoices.Contracts;

/// <param name="IntegrityHash">Changes whenever any field a payment depends on changes (identity, vendor, entity, number, amount, currency, due date, version).</param>
/// <param name="Preparers">People who prepared the invoice or let it through verification: creator, editors, exception resolvers.</param>
public sealed record PayableInvoice(
    Guid Id, Guid LegalEntityId, Guid VendorId, string InvoiceNumber, DateOnly InvoiceDate, DateOnly? DueDate, string CurrencyCode, decimal Total,
    string Status, int Version, int? ApprovedVersion, DateTimeOffset? ApprovedAt, string IntegrityHash, IReadOnlyList<Guid> Preparers)
{
    /// <summary>Approved, and approved for exactly the version that exists now.</summary>
    public bool IsPayable => Status == "Approved" && ApprovedVersion == Version;
}

/// <summary>Read access to invoices for the payment module. The payment module never edits invoices.</summary>
public interface IPayableInvoiceLookup
{
    Task<PayableInvoice?> GetAsync(Guid invoiceId, CancellationToken ct = default);

    /// <summary>Invoices that are currently payable, oldest approval first.</summary>
    Task<IReadOnlyList<PayableInvoice>> ListPayableAsync(Guid? legalEntityId, IReadOnlySet<Guid>? entityScope, int take, CancellationToken ct = default);
}
