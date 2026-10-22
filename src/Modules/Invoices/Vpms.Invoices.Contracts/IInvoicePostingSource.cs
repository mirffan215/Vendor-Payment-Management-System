namespace Vpms.Invoices.Contracts;

public sealed record PostingLine(
    int LineNo, string Description, decimal Quantity, decimal UnitPrice, decimal LineAmount, string? TaxCode, decimal TaxAmount, IReadOnlyDictionary<string, string> Dimensions);

public sealed record PostingAttachment(Guid AttachmentId, string Role, string FileName, string ContentType, string ScanStatus, long SizeBytes);

/// <summary>An invoice as the ERP needs it: the approved data, lines with their dimensions, and the files to attach.</summary>
public sealed record PostingInvoice(
    PayableInvoice Header, string? PoReference, decimal Subtotal, decimal TaxTotal, IReadOnlyList<PostingLine> Lines, IReadOnlyList<PostingAttachment> Attachments);

/// <summary>Read access to approved invoices for ERP posting. The ERP module never edits invoices.</summary>
public interface IInvoicePostingSource
{
    Task<PostingInvoice?> GetAsync(Guid invoiceId, CancellationToken ct = default);

    /// <summary>Invoices of the entity approved at their current version, oldest approval first, from the given approval time on (inclusive, so equal timestamps are not skipped).</summary>
    Task<IReadOnlyList<PostingInvoice>> ListApprovedAsync(Guid legalEntityId, DateTimeOffset? approvedFrom, int take, CancellationToken ct = default);
}
