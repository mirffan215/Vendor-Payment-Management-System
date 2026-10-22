using Vpms.Documents.Contracts;

namespace Vpms.Invoices.Application;

// --- OCR-ready extraction -------------------------------------------------------------------------------------------

public sealed record SuggestedField(string Value, double Confidence);

public sealed record SuggestedLine(string? Description, string? Quantity, string? UnitPrice, string? Amount, double Confidence);

/// <summary>Everything an extractor returns is a suggestion with a confidence; none of it is applied by the system.</summary>
public sealed record ExtractionResult(string Engine, IReadOnlyDictionary<string, SuggestedField> Fields, IReadOnlyList<SuggestedLine> Lines)
{
    public static ExtractionResult Empty(string engine) => new(engine, new Dictionary<string, SuggestedField>(), []);
}

/// <summary>Plug-in point for OCR / document understanding. The default implementation returns nothing.</summary>
public interface IDocumentExtractor
{
    string Engine { get; }

    Task<ExtractionResult> ExtractAsync(Stream content, string fileName, string contentType, CancellationToken ct = default);
}

public sealed record ExtractionDto(Guid Id, Guid InvoiceId, Guid AttachmentId, string Engine, string Status, ExtractionResult Result, DateTimeOffset CreatedAt);

public interface IInvoiceExtractionService
{
    /// <summary>Runs the extractor over scanned-clean Original attachments of draft invoices that have no extraction yet. Returns how many ran.</summary>
    Task<int> RunPendingAsync(int max = 20, CancellationToken ct = default);

    Task<IReadOnlyList<ExtractionDto>> ListAsync(Guid invoiceId, CancellationToken ct = default);

    Task DismissAsync(Guid extractionId, CancellationToken ct = default);
}

/// <summary>Platform-scope listing for the worker.</summary>
public interface IIntakeScan
{
    Task<IReadOnlyList<(Guid TenantId, Guid LegalEntityId)>> EnabledMailboxesAsync(CancellationToken ct = default);

    /// <summary>Tenants with draft invoices whose original file has not been through the extractor yet.</summary>
    Task<IReadOnlyList<Guid>> TenantsWithPendingExtractionAsync(CancellationToken ct = default);
}

// --- mailbox ------------------------------------------------------------------------------------------------------

public sealed record InboundAttachment(string FileName, string ContentType, byte[] Content);

public sealed record InboundMail(string Id, string From, string Subject, DateTimeOffset ReceivedAt, IReadOnlyList<InboundAttachment> Attachments);

/// <summary>Reads supplier mail. The Microsoft Graph implementation and an in-memory fake sit behind this.</summary>
public interface IMailboxReader
{
    /// <summary>Unprocessed messages with attachments, oldest first.</summary>
    Task<IReadOnlyList<InboundMail>> FetchUnprocessedAsync(string mailbox, int max, CancellationToken ct = default);

    /// <summary>Marks the message as handled so it is not returned again (Graph: marks it read).</summary>
    Task MarkProcessedAsync(string mailbox, string messageId, CancellationToken ct = default);
}

public sealed record MailboxDto(Guid LegalEntityId, string Address, bool Enabled, DateTimeOffset? LastPolledAt, string? LastError);

public sealed record IntakeMessageDto(
    Guid Id, Guid LegalEntityId, string FromAddress, string Subject, DateTimeOffset ReceivedAt, string Status, Guid? VendorId, Guid? InvoiceId,
    int Attempts, string? Note, int AttachmentCount);

public sealed record MailboxRunResult(int Fetched, int Created, int Unmatched, int Skipped, int Failed);

public interface IInvoiceIntakeService
{
    /// <summary>Document channel: creates a draft from an uploaded file for a vendor of the entity. Nothing is applied from extraction.</summary>
    Task<InvoiceDto> CreateFromDocumentAsync(Guid legalEntityId, Guid vendorId, string fileName, Stream content, CancellationToken ct = default);

    /// <summary>Email channel for one legal entity: fetch, store, match, create drafts. Idempotent per message id.</summary>
    Task<MailboxRunResult> ProcessMailboxAsync(Guid legalEntityId, CancellationToken ct = default);

    Task<IReadOnlyList<IntakeMessageDto>> ListMessagesAsync(string? status, IReadOnlySet<Guid>? entityScope, int skip, int take, CancellationToken ct = default);

    Task<Guid> EntityOfMessageAsync(Guid messageId, CancellationToken ct = default);

    /// <summary>A person decides which vendor an unmatched email belongs to; the draft is then created as for matched mail.</summary>
    Task<IntakeMessageDto> AssignAsync(Guid messageId, Guid vendorId, CancellationToken ct = default);

    Task<IntakeMessageDto> DismissAsync(Guid messageId, string reason, CancellationToken ct = default);

    Task<MailboxDto?> GetMailboxAsync(Guid legalEntityId, CancellationToken ct = default);

    Task<MailboxDto> SaveMailboxAsync(Guid legalEntityId, string address, bool enabled, CancellationToken ct = default);
}

// --- vendor portal ------------------------------------------------------------------------------------------------

/// <summary>What a vendor is told about its invoice. Verification detail is internal.</summary>
public static class PortalInvoiceStatus
{
    public const string Received = "Received";
    public const string InReview = "InReview";
    public const string NeedsAttention = "NeedsAttention";
    public const string Rejected = "Rejected";
    public const string Cancelled = "Cancelled";

    public static string From(string internalStatus) => internalStatus switch
    {
        Domain.InvoiceStatus.Draft or Domain.InvoiceStatus.Received => Received,
        Domain.InvoiceStatus.Exception => NeedsAttention,
        Domain.InvoiceStatus.Rejected => Rejected,
        Domain.InvoiceStatus.Cancelled => Cancelled,
        _ => InReview,
    };
}

public sealed record PortalInvoiceDto(
    Guid Id, string InvoiceNumber, DateOnly InvoiceDate, DateOnly? DueDate, string CurrencyCode, decimal Total, string? PoReference, string Status,
    string? EntityName, Guid LegalEntityId, DateTimeOffset CreatedAt);

public sealed record PortalSubmitInvoice(Guid LegalEntityId, InvoiceData Data);

public interface IPortalInvoiceService
{
    Task<IReadOnlyList<PortalInvoiceDto>> ListAsync(CancellationToken ct = default);

    Task<PortalInvoiceDto> GetAsync(Guid id, CancellationToken ct = default);

    /// <summary>Creates and submits an invoice for the session vendor, with its document. The vendor id comes from the session only.</summary>
    Task<PortalInvoiceDto> SubmitAsync(PortalSubmitInvoice request, string fileName, Stream content, CancellationToken ct = default);
}
