namespace Vpms.Invoices.Contracts;

public sealed record OpenPayableRow(
    Guid InvoiceId, Guid LegalEntityId, Guid VendorId, string InvoiceNumber, DateOnly InvoiceDate, DateOnly? DueDate, string CurrencyCode, decimal Total, DateTimeOffset? ApprovedAt);

public sealed record ExceptionRow(
    Guid ExceptionId, Guid LegalEntityId, Guid InvoiceId, string InvoiceNumber, string RuleCode, string Severity, string Status, DateTimeOffset CreatedAt, DateTimeOffset? ResolvedAt,
    string? Resolution, Guid? AssignedTo);

public sealed record VatRegisterRow(
    Guid InvoiceId, Guid LegalEntityId, Guid VendorId, string InvoiceNumber, DateOnly InvoiceDate, string? SupplierTaxNumber, string? SupplierTaxInvoiceRef, string CurrencyCode,
    decimal Subtotal, decimal TaxTotal, decimal Total, string TaxCategories, string Status, bool HasWaivedException);

/// <summary>Read-only queries for reports. The entity scope limits rows to the legal entities the caller may see (null = all in the tenant).</summary>
public interface IInvoiceReportSource
{
    /// <summary>Invoices approved at their current version (money still owed unless a payment was released), oldest approval first.</summary>
    Task<IReadOnlyList<OpenPayableRow>> OpenPayablesAsync(Guid? legalEntityId, IReadOnlySet<Guid>? entityScope, int max, CancellationToken ct = default);

    Task<IReadOnlyList<ExceptionRow>> ExceptionsAsync(Guid? legalEntityId, IReadOnlySet<Guid>? entityScope, DateOnly? from, DateOnly? to, int max, CancellationToken ct = default);

    /// <summary>Received supplier invoices dated in the period that were not rejected or cancelled (drafts excluded).</summary>
    Task<IReadOnlyList<VatRegisterRow>> VatRegisterAsync(Guid? legalEntityId, IReadOnlySet<Guid>? entityScope, DateOnly from, DateOnly to, int max, CancellationToken ct = default);
}
