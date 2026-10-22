namespace Vpms.Invoices.Application;

public sealed record InvoiceLineInput(
    string Description, decimal Quantity, decimal UnitPrice, decimal LineAmount, string? TaxCode, decimal TaxRate, decimal TaxAmount,
    string? PoLineRef, string? DimensionsJson);

public sealed record InvoiceLineDto(
    Guid Id, int LineNo, string Description, decimal Quantity, decimal UnitPrice, decimal LineAmount, string? TaxCode, decimal TaxRate,
    decimal TaxAmount, string? PoLineRef, string? DimensionsJson);

public sealed record InvoiceData(
    string InvoiceNumber, DateOnly InvoiceDate, DateOnly? DueDate, string CurrencyCode, decimal Subtotal, decimal TaxTotal, decimal Total,
    string? PoReference, string? SupplierTaxInvoiceRef, IReadOnlyList<InvoiceLineInput> Lines, string? SupplierTaxNumber = null);

public sealed record CreateInvoice(Guid LegalEntityId, Guid VendorId, InvoiceData Data);

public sealed record InvoiceAttachmentDto(Guid Id, Guid AttachmentId, string Role, string FileName, string ScanStatus, long SizeBytes, DateTimeOffset CreatedAt);

public sealed record InvoiceDto(
    Guid Id, Guid LegalEntityId, Guid VendorId, string VendorName, string InvoiceNumber, DateOnly InvoiceDate, DateOnly? DueDate, string CurrencyCode,
    decimal Subtotal, decimal TaxTotal, decimal Total, string? PoReference, string? SupplierTaxInvoiceRef, string Source, string Status, string? StatusReason,
    int Version, DateTimeOffset CreatedAt, DateTimeOffset? ReceivedAt, IReadOnlyList<InvoiceLineDto> Lines, IReadOnlyList<InvoiceAttachmentDto> Attachments, string? SupplierTaxNumber = null,
    DateTimeOffset? ApprovedAt = null, int? ApprovedVersion = null);

public sealed record InvoiceListItem(
    Guid Id, Guid LegalEntityId, Guid VendorId, string VendorName, string InvoiceNumber, DateOnly InvoiceDate, string CurrencyCode, decimal Total,
    string Source, string Status, DateTimeOffset CreatedAt);

public sealed record InvoiceFilter(string? Status, Guid? VendorId, Guid? LegalEntityId, string? Search, string? Source);

public interface IInvoiceService
{
    /// <param name="entityScope">null = all legal entities; otherwise only invoices of these entities.</param>
    Task<IReadOnlyList<InvoiceListItem>> ListAsync(InvoiceFilter filter, IReadOnlySet<Guid>? entityScope, int skip, int take, CancellationToken ct = default);

    Task<InvoiceDto> GetAsync(Guid id, CancellationToken ct = default);

    /// <summary>The legal entity an invoice belongs to, so callers can check entity-scoped permissions. 404 when unknown.</summary>
    Task<Guid> EntityOfAsync(Guid id, CancellationToken ct = default);

    Task<InvoiceDto> CreateAsync(CreateInvoice request, CancellationToken ct = default);

    Task<InvoiceDto> UpdateAsync(Guid id, InvoiceData data, CancellationToken ct = default);

    Task<InvoiceDto> SubmitAsync(Guid id, CancellationToken ct = default);

    /// <summary>Starts the approval workflow of the legal entity for a verified invoice. The person doing this can never approve it.</summary>
    Task<InvoiceDto> SendForApprovalAsync(Guid id, CancellationToken ct = default);

    Task<InvoiceDto> CancelAsync(Guid id, string reason, CancellationToken ct = default);

    Task<InvoiceDto> RejectAsync(Guid id, string reason, CancellationToken ct = default);
}

public interface IInvoiceAttachmentService
{
    Task<InvoiceAttachmentDto> AddAsync(Guid invoiceId, string role, Guid attachmentId, CancellationToken ct = default);

    Task<Vpms.Documents.Contracts.AttachmentDownload> OpenAsync(Guid invoiceId, Guid invoiceAttachmentId, CancellationToken ct = default);
}
