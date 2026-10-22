namespace Vpms.ErpIntegration.Contracts;

/// <summary>How to reach one ERP: resolved settings for a tenant. The secret is held only in memory for the duration of a call.</summary>
public sealed record ErpConnectionSettings(
    string BaseUrl, string AuthorityTenantId, string ClientId, string ClientSecret, IReadOnlyDictionary<string, string> EntitySets, int PageSize, int TimeoutSeconds);

/// <summary>One VPMS dimension mapped to an ERP financial dimension.</summary>
public sealed record DimensionMapping(string VpmsDimension, string ErpDimension, string? DefaultValue, IReadOnlyDictionary<string, string> ValueMap);

/// <summary>How one legal entity maps into its ERP company.</summary>
public sealed record ErpCompanyMapping(
    string Company, string JournalName, string? PostingProfile, IReadOnlyDictionary<string, string> PaymentTermsMap, IReadOnlyDictionary<string, string> TaxCodeMap,
    IReadOnlyList<DimensionMapping> Dimensions);

public sealed record ErpContext(ErpConnectionSettings Connection, ErpCompanyMapping Company);

public enum ErpOutcome
{
    Succeeded,

    /// <summary>The ERP already has the document or record for this idempotency key; nothing was created. The reference is the existing one.</summary>
    AlreadyExists,

    /// <summary>The ERP refused permanently (validation, unknown vendor, closed period). Retrying the same message will not help.</summary>
    Rejected,

    /// <summary>Temporary failure (network, throttling, server error). Retry later, honouring <see cref="ErpResult.RetryAfter"/>.</summary>
    Transient,
}

public sealed record ErpResult(ErpOutcome Outcome, string? Reference, string? ErrorCode = null, string? Message = null, TimeSpan? RetryAfter = null)
{
    public bool IsSuccess => Outcome is ErpOutcome.Succeeded or ErpOutcome.AlreadyExists;

    public static ErpResult Ok(string reference) => new(ErpOutcome.Succeeded, reference);

    public static ErpResult Existing(string reference) => new(ErpOutcome.AlreadyExists, reference);

    public static ErpResult Rejected(string code, string? message = null) => new(ErpOutcome.Rejected, null, code, message);

    public static ErpResult Transient(string code, string? message = null, TimeSpan? retryAfter = null) => new(ErpOutcome.Transient, null, code, message, retryAfter);
}

public sealed record ErpQuery<T>(ErpOutcome Outcome, IReadOnlyList<T> Items, string? ErrorCode = null, TimeSpan? RetryAfter = null)
{
    public bool IsSuccess => Outcome == ErpOutcome.Succeeded;

    public static ErpQuery<T> Of(IReadOnlyList<T> items) => new(ErpOutcome.Succeeded, items);

    public static ErpQuery<T> Failed(ErpOutcome outcome, string code, TimeSpan? retryAfter = null) => new(outcome, [], code, retryAfter);
}

public sealed record ErpVendor(Guid VpmsVendorId, string Name, string? TaxNumber, string CountryCode, string CurrencyCode, string? PaymentTermsCode, string? ExistingAccount);

public sealed record ErpVendorRecord(string Account, string Name, string? TaxNumber, string CurrencyCode, bool Blocked);

public sealed record ErpInvoiceLine(int LineNo, string Description, decimal Quantity, decimal UnitPrice, decimal Amount, string? TaxCode, IReadOnlyDictionary<string, string> Dimensions);

/// <param name="DocumentKey">The idempotency key, stored on the ERP document so a replay can find it.</param>
public sealed record ErpInvoice(
    string DocumentKey, string VendorAccount, string InvoiceNumber, DateOnly InvoiceDate, DateOnly? DueDate, string CurrencyCode, decimal Total, decimal TaxTotal,
    IReadOnlyList<ErpInvoiceLine> Lines);

public sealed record ErpAttachment(string FileName, string ContentType, byte[] Content);

public static class ErpDocumentState
{
    public const string Open = "Open";
    public const string Posted = "Posted";
    public const string Paid = "Paid";
    public const string Cancelled = "Cancelled";
}

public sealed record ErpInvoiceStatus(
    string Reference, string? DocumentKey, string VendorAccount, string InvoiceNumber, decimal Total, string CurrencyCode, DateOnly InvoiceDate, string State, DateTimeOffset? PaidAt);

public sealed record ErpPayment(string Reference, string VendorAccount, string? InvoiceReference, decimal Amount, string CurrencyCode, DateTimeOffset PaidAt);

public sealed record ErpPurchaseOrderLine(string LineNo, string Description, decimal OrderedQuantity, decimal UnitPrice, decimal InvoicedQuantity);

public sealed record ErpPurchaseOrder(
    string PoNumber, int Version, string VendorAccount, string CurrencyCode, string Status, DateOnly OrderDate, decimal InvoicedValue, IReadOnlyList<ErpPurchaseOrderLine> Lines);

public sealed record ErpDimensionValue(string Dimension, string Value, string? Description);

/// <summary>
/// Everything VPMS does with an ERP. Capability-based and provider-neutral: core modules never see D365 types. Every write carries an
/// idempotency key and is safe to repeat; implementations report failures as typed outcomes instead of throwing, so the queue can decide
/// between retrying and dead-lettering.
/// </summary>
public interface IErpAdapter
{
    /// <summary>The provider name this adapter serves, as stored in the connection settings (for example "D365Finance").</summary>
    string Provider { get; }

    /// <summary>
    /// What this ERP shows for a document created with the idempotency key (the key itself, or a short reference derived from it when the
    /// ERP's fields are too small). Reconciliation matches listed documents to VPMS messages by this value.
    /// </summary>
    string DocumentReference(string idempotencyKey);

    Task<ErpResult> ValidateConnectionAsync(ErpContext context, CancellationToken ct = default);

    /// <summary>Creates or updates the vendor in the ERP and returns its account number.</summary>
    Task<ErpResult> UpsertVendorAsync(ErpContext context, ErpVendor vendor, string idempotencyKey, CancellationToken ct = default);

    Task<ErpQuery<ErpVendorRecord>> ListVendorsAsync(ErpContext context, CancellationToken ct = default);

    /// <summary>Creates the vendor invoice (header and lines) unless a document with this key already exists.</summary>
    Task<ErpResult> CreateVendorInvoiceAsync(ErpContext context, ErpInvoice invoice, CancellationToken ct = default);

    /// <summary>Attaches a document to an ERP invoice; the idempotency key makes a repeat a no-op.</summary>
    Task<ErpResult> AttachDocumentAsync(ErpContext context, string invoiceReference, ErpAttachment attachment, string idempotencyKey, CancellationToken ct = default);

    /// <summary>All vendor invoices the ERP holds for the company, optionally only those changed since a time. Used by reconciliation.</summary>
    Task<ErpQuery<ErpInvoiceStatus>> ListInvoicesAsync(ErpContext context, DateTimeOffset? changedSince, CancellationToken ct = default);

    Task<ErpQuery<ErpPayment>> GetVendorPaymentsAsync(ErpContext context, DateTimeOffset since, CancellationToken ct = default);

    Task<ErpQuery<ErpPurchaseOrder>> ListPurchaseOrdersAsync(ErpContext context, DateTimeOffset? changedSince, CancellationToken ct = default);

    Task<ErpQuery<ErpDimensionValue>> GetDimensionValuesAsync(ErpContext context, string dimension, CancellationToken ct = default);
}
