using System.ComponentModel.DataAnnotations;
using Vpms.BuildingBlocks.Errors;
using Vpms.BuildingBlocks.Tenancy;

namespace Vpms.Invoices.Domain.Verification;

/// <summary>The recorded outcome of one rule in one verification run. Kept for every run, so the record shows what was checked and how.</summary>
public class VerificationResult : ILegalEntityScoped, IVendorOwned
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid LegalEntityId { get; set; }
    public Guid VendorId { get; set; }
    public Guid InvoiceId { get; set; }
    public Guid RunId { get; set; }
    public int InvoiceVersion { get; set; }

    /// <summary>The rule-set version the run used (0 = built-in defaults).</summary>
    public int RuleSetVersion { get; set; }

    public string RuleCode { get; set; } = "";
    public string Outcome { get; set; } = "";
    public string Severity { get; set; } = "";
    public string DetailJson { get; set; } = "[]";
    public DateTimeOffset CreatedAt { get; set; }
}

public static class ExceptionStatus
{
    public const string Open = "Open";
    public const string Resolved = "Resolved";
    public const string Waived = "Waived";
}

/// <summary>A failed rule that a person has to look at. Closed when the invoice is corrected (Resolved) or a person accepts it (Waived).</summary>
public class InvoiceException : ILegalEntityScoped, IVendorOwned
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid LegalEntityId { get; set; }
    public Guid VendorId { get; set; }
    public Guid InvoiceId { get; set; }
    public string RuleCode { get; set; } = "";
    public string Severity { get; set; } = "";
    public string Status { get; set; } = ExceptionStatus.Open;
    public string DetailJson { get; set; } = "[]";

    /// <summary>The version of the invoice the exception was raised on. A waiver only counts for that version.</summary>
    public int InvoiceVersion { get; set; }

    public Guid? AssignedTo { get; set; }
    public Guid? ResolvedBy { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
    public string? Resolution { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    [Timestamp]
    public byte[] RowVersion { get; set; } = [];

    public void Waive(Guid actor, string reason, DateTimeOffset now)
    {
        if (Status != ExceptionStatus.Open) throw new ConflictException("invalid_transition", "This exception is already closed.");
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 500) throw new ValidationFailedException("reason_required", "A reason is required (max 500 characters).");
        Status = ExceptionStatus.Waived;
        ResolvedBy = actor;
        ResolvedAt = now;
        Resolution = reason.Trim();
    }

    /// <summary>Closed by verification itself because the rule now passes.</summary>
    public void ResolveByVerification(DateTimeOffset now)
    {
        if (Status != ExceptionStatus.Open) return;
        Status = ExceptionStatus.Resolved;
        ResolvedAt = now;
        Resolution = "corrected";
    }
}

public static class PurchaseOrderStatus
{
    public const string Open = "Open";
    public const string Closed = "Closed";
    public const string Cancelled = "Cancelled";

    public static readonly IReadOnlyList<string> All = [Open, Closed, Cancelled];
}

/// <summary>A purchase order the customer issued, imported from the ERP (or by hand until the ERP integration exists).</summary>
public class PurchaseOrder : ILegalEntityScoped, IVendorOwned
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid LegalEntityId { get; set; }
    public Guid VendorId { get; set; }
    public string PoNumber { get; set; } = "";
    public int Version { get; set; } = 1;
    public string CurrencyCode { get; set; } = "";
    public string Status { get; set; } = PurchaseOrderStatus.Open;
    public DateOnly OrderDate { get; set; }

    /// <summary>Value already invoiced against the order outside VPMS (history imported from the ERP).</summary>
    public decimal PreviouslyInvoicedValue { get; set; }

    public string ContentHash { get; set; } = "";
    public Guid ImportedBy { get; set; }
    public DateTimeOffset ImportedAt { get; set; }
    public List<PurchaseOrderLine> Lines { get; set; } = [];

    [Timestamp]
    public byte[] RowVersion { get; set; } = [];
}

public class PurchaseOrderLine : IVendorOwned
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid VendorId { get; set; }
    public Guid PurchaseOrderId { get; set; }
    public string LineNo { get; set; } = "";
    public string Description { get; set; } = "";
    public decimal OrderedQuantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal PreviouslyInvoicedQuantity { get; set; }
}
