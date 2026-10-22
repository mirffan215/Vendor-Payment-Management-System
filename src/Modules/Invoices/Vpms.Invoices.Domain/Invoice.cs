using System.ComponentModel.DataAnnotations;
using Vpms.BuildingBlocks.Errors;
using Vpms.BuildingBlocks.Tenancy;

namespace Vpms.Invoices.Domain;

public static class InvoiceStatus
{
    public const string Draft = "Draft";
    public const string Received = "Received";
    public const string InVerification = "InVerification";
    public const string Exception = "Exception";
    public const string Verified = "Verified";
    public const string ReadyForApproval = "ReadyForApproval";
    public const string InApproval = "InApproval";
    public const string Approved = "Approved";
    public const string Returned = "Returned";
    public const string Rejected = "Rejected";
    public const string Cancelled = "Cancelled";
}

public static class InvoiceSource
{
    public const string Manual = "Manual";
    public const string Portal = "Portal";
    public const string Email = "Email";
    public const string Document = "Document";

    public static readonly IReadOnlyList<string> All = [Manual, Portal, Email, Document];
}

public static class AttachmentRole
{
    public const string Original = "Original";
    public const string Supporting = "Supporting";

    public static readonly IReadOnlyList<string> All = [Original, Supporting];
}

/// <summary>
/// A supplier invoice received by the customer (VPMS never issues invoices). All status changes go through the methods
/// below, which enforce the allowed transitions. Approval states arrive with the workflow engine in Phase 4.
/// </summary>
public class Invoice : ILegalEntityScoped, IVendorOwned
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid LegalEntityId { get; set; }
    public Guid VendorId { get; set; }

    public string InvoiceNumber { get; set; } = "";
    public DateOnly InvoiceDate { get; set; }
    public DateOnly? DueDate { get; set; }
    public string CurrencyCode { get; set; } = "";
    public decimal Subtotal { get; set; }
    public decimal TaxTotal { get; set; }
    public decimal Total { get; set; }
    public string? PoReference { get; set; }

    /// <summary>The supplier's own tax-invoice reference (for example a ZATCA UUID). Informational only.</summary>
    public string? SupplierTaxInvoiceRef { get; set; }

    /// <summary>The supplier's tax registration number as printed on the invoice (for example the Saudi VAT number).</summary>
    public string? SupplierTaxNumber { get; set; }

    public string Source { get; set; } = InvoiceSource.Manual;
    public string Status { get; set; } = InvoiceStatus.Draft;

    /// <summary>Incremented on every change to the invoice data after creation.</summary>
    public int Version { get; set; } = 1;

    /// <summary>Keyed hash of vendor + normalised number + total + date (exact duplicate lookup).</summary>
    public string? DuplicateKeyIndex { get; set; }

    /// <summary>Keyed hash of vendor + normalised number (same-number lookup, the basis of fuzzy duplicate checks).</summary>
    public string? NumberKeyIndex { get; set; }

    public string? StatusReason { get; set; }
    public Guid CreatedBy { get; set; }
    public string CreatedByKind { get; set; } = "Staff";
    public DateTimeOffset CreatedAt { get; set; }
    public Guid? LastEditedBy { get; set; }
    public DateTimeOffset? LastEditedAt { get; set; }
    public DateTimeOffset? ReceivedAt { get; set; }

    /// <summary>The approval run in force or last run for this invoice (Workflow module).</summary>
    public Guid? ApprovalRunId { get; set; }

    /// <summary>The invoice version that was approved. Anything that follows (payment) must still see this version.</summary>
    public int? ApprovedVersion { get; set; }

    public DateTimeOffset? ApprovedAt { get; set; }

    /// <summary>The material fields as they were when approval started. Later edits are compared with this, so small edits cannot add up unnoticed.</summary>
    public string? ApprovalBaselineJson { get; set; }

    public List<InvoiceLine> Lines { get; set; } = [];

    [Timestamp]
    public byte[] RowVersion { get; set; } = [];

    public bool IsEditable => Status is InvoiceStatus.Draft or InvoiceStatus.Exception or InvoiceStatus.Returned;

    /// <summary>Replaces the data of an editable invoice. An invoice in Exception goes back to Received for re-verification.</summary>
    public void BeginEdit(Guid editor, DateTimeOffset now)
    {
        Require(IsEditable, "Only a draft invoice, or one with exceptions, can be changed.");
        Version++;
        LastEditedBy = editor;
        LastEditedAt = now;
        if (Status is InvoiceStatus.Exception or InvoiceStatus.Returned)
        {
            Status = InvoiceStatus.Received;
            StatusReason = null;
        }
    }

    public void Submit(DateTimeOffset now)
    {
        Require(Status == InvoiceStatus.Draft, "Only a draft invoice can be submitted.");
        Status = InvoiceStatus.Received;
        ReceivedAt = now;
    }

    public void StartVerification()
    {
        Require(Status == InvoiceStatus.Received, "Only a received invoice can be verified.");
        Status = InvoiceStatus.InVerification;
    }

    public void MarkVerified()
    {
        Require(Status == InvoiceStatus.InVerification, "Only an invoice in verification can be marked verified.");
        Status = InvoiceStatus.Verified;
        StatusReason = null;
    }

    public void MarkException(string? reason = null)
    {
        Require(Status == InvoiceStatus.InVerification, "Only an invoice in verification can raise exceptions.");
        Status = InvoiceStatus.Exception;
        StatusReason = reason;
    }

    /// <summary>Every exception has been closed without a change to the invoice (all waived), so verification is complete.</summary>
    public void ClearExceptions()
    {
        Require(Status == InvoiceStatus.Exception, "Only an invoice with exceptions can have them cleared.");
        Status = InvoiceStatus.Verified;
        StatusReason = null;
    }

    /// <summary>Verify again with the same invoice data, for example after the rules or a purchase order changed.</summary>
    public void RequestReverification()
    {
        Require(Status is InvoiceStatus.Exception or InvoiceStatus.Verified, "Only a verified invoice, or one with exceptions, can be verified again.");
        Status = InvoiceStatus.Received;
        StatusReason = null;
    }

    public void MarkReadyForApproval()
    {
        Require(Status == InvoiceStatus.Verified, "Only a verified invoice can be sent for approval.");
        Status = InvoiceStatus.ReadyForApproval;
    }

    public bool CanStartApproval => Status is InvoiceStatus.Verified or InvoiceStatus.ReadyForApproval;

    public void StartApproval(Guid runId, string baselineJson)
    {
        Require(CanStartApproval, "Only a verified invoice can be sent for approval.");
        Status = InvoiceStatus.InApproval;
        StatusReason = null;
        ApprovalRunId = runId;
        ApprovalBaselineJson = baselineJson;
    }

    public bool CanRevise => Status is InvoiceStatus.InApproval or InvoiceStatus.Approved;

    /// <summary>Starts an edit of an invoice that is in approval or approved. <see cref="ConcludeRevision"/> then says whether the approval survives.</summary>
    public void BeginRevision(Guid editor, DateTimeOffset now)
    {
        Require(CanRevise, "Only an invoice in approval or already approved can be revised.");
        Version++;
        LastEditedBy = editor;
        LastEditedAt = now;
    }

    /// <summary>A material change takes the invoice back to verification and voids the approval; a small one keeps it, now covering the new version.</summary>
    public void ConcludeRevision(bool material)
    {
        if (material)
        {
            Status = InvoiceStatus.Received;
            StatusReason = null;
            ApprovedAt = null;
            ApprovedVersion = null;
            ApprovalBaselineJson = null;
        }
        else if (Status == InvoiceStatus.Approved)
        {
            ApprovedVersion = Version;
        }
    }

    /// <summary>Applies the end of an approval run. Repeating it for the same version changes nothing.</summary>
    public void ApplyApproved(int version, DateTimeOffset now)
    {
        if (Status == InvoiceStatus.Approved && ApprovedVersion == version) return;
        Require(Status == InvoiceStatus.InApproval && Version == version, "Only the version that went through approval can be approved.");
        Status = InvoiceStatus.Approved;
        StatusReason = null;
        ApprovedVersion = version;
        ApprovedAt = now;
    }

    public void ApplyRejectedByApproval(string? reason)
    {
        if (Status == InvoiceStatus.Rejected) return;
        Require(Status == InvoiceStatus.InApproval, "Only an invoice in approval can be rejected by its approvers.");
        Status = InvoiceStatus.Rejected;
        StatusReason = Clip(reason);
    }

    public void ApplyReturnedByApproval(string? reason)
    {
        if (Status == InvoiceStatus.Returned) return;
        Require(Status == InvoiceStatus.InApproval, "Only an invoice in approval can be returned by its approvers.");
        Status = InvoiceStatus.Returned;
        StatusReason = Clip(reason);
    }

    private static string? Clip(string? reason) => string.IsNullOrWhiteSpace(reason) ? null : reason.Trim()[..Math.Min(reason.Trim().Length, 500)];

    public void Reject(string reason)
    {
        Require(Status is InvoiceStatus.Exception or InvoiceStatus.Verified or InvoiceStatus.Received or InvoiceStatus.Returned, "This invoice cannot be rejected in its current status.");
        RequireReason(reason);
        Status = InvoiceStatus.Rejected;
        StatusReason = reason.Trim();
    }

    public void Cancel(string reason)
    {
        Require(Status is InvoiceStatus.Draft or InvoiceStatus.Received or InvoiceStatus.Exception or InvoiceStatus.Verified or InvoiceStatus.Returned,
            "This invoice cannot be cancelled in its current status.");
        RequireReason(reason);
        Status = InvoiceStatus.Cancelled;
        StatusReason = reason.Trim();
    }

    private void Require(bool condition, string message)
    {
        if (!condition) throw new ConflictException("invalid_transition", message);
    }

    private static void RequireReason(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 500)
        {
            throw new ValidationFailedException("reason_required", "A reason is required (max 500 characters).");
        }
    }
}

public class InvoiceLine : IVendorOwned
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid VendorId { get; set; }
    public Guid InvoiceId { get; set; }
    public int LineNo { get; set; }
    public string Description { get; set; } = "";
    public decimal Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal LineAmount { get; set; }
    public string? TaxCode { get; set; }
    public decimal TaxRate { get; set; }
    public decimal TaxAmount { get; set; }
    public string? PoLineRef { get; set; }
    public string? DimensionsJson { get; set; }
}

public class InvoiceAttachment : IVendorOwned
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid VendorId { get; set; }
    public Guid InvoiceId { get; set; }
    public Guid AttachmentId { get; set; }
    public string Role { get; set; } = AttachmentRole.Supporting;
    public Guid? AddedBy { get; set; }
    public string AddedByKind { get; set; } = "Staff";
    public DateTimeOffset CreatedAt { get; set; }
}
