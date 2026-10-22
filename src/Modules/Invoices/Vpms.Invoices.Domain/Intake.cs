using Vpms.BuildingBlocks.Tenancy;

namespace Vpms.Invoices.Domain;

public static class ExtractionStatus
{
    public const string Suggested = "Suggested";
    public const string Dismissed = "Dismissed";
}

/// <summary>
/// What a document extractor (OCR) thinks an invoice file says. It is only ever a suggestion: nothing in it is copied into
/// the invoice automatically, a person reads it and enters or corrects the invoice.
/// </summary>
public class InvoiceExtraction : IVendorOwned
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid VendorId { get; set; }
    public Guid InvoiceId { get; set; }
    public Guid AttachmentId { get; set; }
    public string Engine { get; set; } = "";
    public string Status { get; set; } = ExtractionStatus.Suggested;
    public string ResultJson { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>The mailbox a legal entity receives supplier invoices in. Polled by the worker.</summary>
public class InvoiceMailbox : ILegalEntityScoped
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid LegalEntityId { get; set; }
    public string Address { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public DateTimeOffset? LastPolledAt { get; set; }
    public string? LastError { get; set; }
    public Guid UpdatedBy { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public static class IntakeStatus
{
    public const string Received = "Received";
    public const string Processed = "Processed";
    public const string Unmatched = "Unmatched";
    public const string Failed = "Failed";
    public const string DeadLetter = "DeadLetter";
    public const string Dismissed = "Dismissed";

    public static bool IsFinal(string status) => status is Processed or Unmatched or DeadLetter or Dismissed;
}

/// <summary>One received email. The unique message id makes intake idempotent: a replayed message never creates a second invoice.</summary>
public class IntakeMessage : ILegalEntityScoped
{
    public const int MaxAttempts = 3;

    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid LegalEntityId { get; set; }
    public string ExternalMessageId { get; set; } = "";
    public string FromAddress { get; set; } = "";
    public string Subject { get; set; } = "";
    public DateTimeOffset ReceivedAt { get; set; }
    public string Status { get; set; } = IntakeStatus.Received;
    public Guid? VendorId { get; set; }
    public Guid? InvoiceId { get; set; }
    public int Attempts { get; set; }
    public string? LastError { get; set; }
    public string? Note { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ProcessedAt { get; set; }

    /// <summary>Attachment ids stored in quarantine for this message (comma separated), kept so an unmatched message can be assigned later.</summary>
    public string AttachmentIds { get; set; } = "";
}
