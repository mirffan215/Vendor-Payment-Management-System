namespace Vpms.Payments.Application;

public sealed record PaymentRequestDto(
    Guid Id, Guid LegalEntityId, Guid VendorId, string VendorName, Guid InvoiceId, string InvoiceNumber, int InvoiceVersion, decimal Amount, string CurrencyCode,
    DateOnly DueDate, string Status, string? StatusReason, Guid? ProposalId, Guid CreatedBy, DateTimeOffset CreatedAt);

/// <param name="Blockers">Why a payment request cannot be created yet (stable codes), empty when it can.</param>
public sealed record PayableInvoiceDto(
    Guid InvoiceId, Guid LegalEntityId, Guid VendorId, string VendorName, string InvoiceNumber, DateOnly InvoiceDate, DateOnly DueDate, string CurrencyCode, decimal Total,
    DateTimeOffset? ApprovedAt, bool HasOpenRequest, IReadOnlyList<string> Blockers);

public sealed record RequestResult(Guid InvoiceId, PaymentRequestDto? Request, string? ErrorCode);

public sealed record PaymentRequestFilter(string? Status, Guid? LegalEntityId, Guid? VendorId);

public interface IPaymentRequestService
{
    /// <summary>Approved invoices that are not yet claimed by a payment request, with the reasons any of them cannot be requested yet.</summary>
    Task<IReadOnlyList<PayableInvoiceDto>> ListPayableAsync(Guid? legalEntityId, IReadOnlySet<Guid>? entityScope, CancellationToken ct = default);

    Task<IReadOnlyList<PaymentRequestDto>> ListAsync(PaymentRequestFilter filter, IReadOnlySet<Guid>? entityScope, int skip, int take, CancellationToken ct = default);

    /// <summary>Creates a request for each invoice. One invoice failing never stops the others; each result says what happened.</summary>
    Task<IReadOnlyList<RequestResult>> CreateAsync(IReadOnlyList<Guid> invoiceIds, CancellationToken ct = default);

    Task<PaymentRequestDto> CancelAsync(Guid requestId, string reason, CancellationToken ct = default);

    Task<Guid> EntityOfAsync(Guid requestId, CancellationToken ct = default);

    /// <summary>The legal entity of an invoice, so callers can check entity-scoped permissions before creating requests.</summary>
    Task<Guid> EntityOfInvoiceAsync(Guid invoiceId, CancellationToken ct = default);
}
