using Vpms.Invoices.Domain.Verification;

namespace Vpms.Invoices.Application;

public sealed record FindingDto(string Key, IReadOnlyDictionary<string, string> Evidence);

public sealed record VerificationResultDto(string RuleCode, string Outcome, string Severity, IReadOnlyList<FindingDto> Findings);

public sealed record ExceptionDto(
    Guid Id, Guid InvoiceId, string InvoiceNumber, Guid LegalEntityId, Guid VendorId, string RuleCode, string Severity, string Status,
    IReadOnlyList<FindingDto> Findings, int InvoiceVersion, Guid? AssignedTo, Guid? ResolvedBy, DateTimeOffset? ResolvedAt, string? Resolution, DateTimeOffset CreatedAt);

public sealed record VerificationDetailDto(
    Guid InvoiceId, string InvoiceStatus, int InvoiceVersion, int RuleSetVersion, DateTimeOffset? VerifiedAt,
    IReadOnlyList<VerificationResultDto> Results, IReadOnlyList<ExceptionDto> Exceptions);

public sealed record VerificationOutcome(string InvoiceStatus, int Failed, int Open, int RuleSetVersion);

public interface IInvoiceVerifier
{
    /// <summary>
    /// Runs the entity's rules over a received invoice and moves it to Verified or Exception. Safe to call twice: an invoice that is
    /// not waiting for verification is left as it is.
    /// </summary>
    Task<VerificationOutcome> VerifyAsync(Guid invoiceId, CancellationToken ct = default);
}

public interface IVerificationScan
{
    /// <summary>Tenants with invoices that have been waiting for verification longer than the grace period (a safety net for the worker).</summary>
    Task<IReadOnlyList<Guid>> TenantsWithUnverifiedInvoicesAsync(TimeSpan olderThan, CancellationToken ct = default);

    Task<int> VerifyStaleAsync(TimeSpan olderThan, int max = 50, CancellationToken ct = default);
}

public interface IInvoiceExceptionService
{
    Task<IReadOnlyList<ExceptionDto>> ListAsync(string? status, Guid? assignedTo, IReadOnlySet<Guid>? entityScope, int skip, int take, CancellationToken ct = default);

    Task<Guid> EntityOfAsync(Guid exceptionId, CancellationToken ct = default);

    Task<VerificationDetailDto> DetailAsync(Guid invoiceId, CancellationToken ct = default);

    /// <summary>
    /// A person accepts a failed check with a reason. Flags need Invoice.Exception.Resolve; blocks need Invoice.Exception.Override and
    /// a person other than the one who created or last edited the invoice. Checks on the vendor's status can never be waived.
    /// </summary>
    Task<ExceptionDto> WaiveAsync(Guid exceptionId, string reason, CancellationToken ct = default);

    Task<ExceptionDto> AssignAsync(Guid exceptionId, Guid? userId, CancellationToken ct = default);

    Task<VerificationDetailDto> ReverifyAsync(Guid invoiceId, CancellationToken ct = default);
}

// --- purchase orders ----------------------------------------------------------------------------------------------

public sealed record PoLineInput(string LineNo, string Description, decimal OrderedQuantity, decimal UnitPrice, decimal PreviouslyInvoicedQuantity);

public sealed record PoImportItem(
    string PoNumber, int Version, Guid? VendorId, string? ErpVendorAccount, string CurrencyCode, string Status, DateOnly OrderDate,
    decimal PreviouslyInvoicedValue, IReadOnlyList<PoLineInput> Lines);

public sealed record PoImportError(string PoNumber, string Code, string Message);

public sealed record PoImportResult(int Created, int Updated, int Unchanged, IReadOnlyList<PoImportError> Errors);

public sealed record PurchaseOrderLineDto(string LineNo, string Description, decimal OrderedQuantity, decimal UnitPrice, decimal PreviouslyInvoicedQuantity);

public sealed record PurchaseOrderDto(
    Guid Id, Guid LegalEntityId, Guid VendorId, string PoNumber, int Version, string CurrencyCode, string Status, DateOnly OrderDate,
    decimal PreviouslyInvoicedValue, IReadOnlyList<PurchaseOrderLineDto> Lines);

public interface IPurchaseOrderService
{
    /// <summary>Idempotent: the same version with the same content changes nothing, a higher version replaces the order, an older or conflicting one is refused.</summary>
    Task<PoImportResult> ImportAsync(Guid legalEntityId, IReadOnlyList<PoImportItem> items, CancellationToken ct = default);

    Task<IReadOnlyList<PurchaseOrderDto>> ListAsync(Guid? legalEntityId, string? search, IReadOnlySet<Guid>? entityScope, int skip, int take, CancellationToken ct = default);

    Task<PurchaseOrderDto> GetAsync(Guid id, CancellationToken ct = default);

    Task<Guid> EntityOfAsync(Guid id, CancellationToken ct = default);
}
