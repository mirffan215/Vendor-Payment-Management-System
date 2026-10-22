using System.ComponentModel.DataAnnotations;
using Vpms.BuildingBlocks.Errors;
using Vpms.BuildingBlocks.Security;
using Vpms.BuildingBlocks.Tenancy;

namespace Vpms.Payments.Domain;

public static class ProposalStatus
{
    public const string Draft = "Draft";
    public const string PendingApproval = "PendingApproval";
    public const string Approved = "Approved";
    public const string Released = "Released";
    public const string Invalidated = "Invalidated";
    public const string Cancelled = "Cancelled";

    /// <summary>The proposal still holds its requests.</summary>
    public static readonly IReadOnlyList<string> Live = [Draft, PendingApproval, Approved];
}

/// <summary>Payables of one vendor, legal entity, currency and due date, prepared together. Approval and release are separate steps by different people.</summary>
public class PaymentProposal : ILegalEntityScoped
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid LegalEntityId { get; set; }

    /// <summary>Sequential per legal entity.</summary>
    public int SequenceNo { get; set; }

    public string ProposalNo { get; set; } = "";
    public Guid VendorId { get; set; }
    public DateOnly DueDate { get; set; }
    public string CurrencyCode { get; set; } = "";
    public decimal TotalAmount { get; set; }
    public string Status { get; set; } = ProposalStatus.Draft;
    public string? StatusReason { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Guid? SubmittedBy { get; set; }
    public DateTimeOffset? SubmittedAt { get; set; }
    public Guid? ApprovedBy { get; set; }
    public DateTimeOffset? ApprovedAt { get; set; }
    public string? ApprovalComment { get; set; }
    public Guid? ReleasedBy { get; set; }
    public DateTimeOffset? ReleasedAt { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }

    public List<PaymentProposalItem> Items { get; set; } = [];

    [Timestamp]
    public byte[] RowVersion { get; set; } = [];

    public static string FormatNo(int sequenceNo, DateTimeOffset at) => $"PP-{at.Year}-{sequenceNo:D6}";

    public void Submit(Guid by, DateTimeOffset now)
    {
        Require(Status == ProposalStatus.Draft, "Only a draft proposal can be submitted for approval.");
        if (Items.Count == 0) throw new ConflictException("empty_proposal", "A proposal needs at least one payment request.");
        Status = ProposalStatus.PendingApproval;
        SubmittedBy = by;
        SubmittedAt = now;
    }

    /// <summary>Treasury approval. The person who made or submitted the proposal can never approve it.</summary>
    public void Approve(Guid approver, string? comment, DateTimeOffset now)
    {
        Require(Status == ProposalStatus.PendingApproval, "Only a proposal waiting for approval can be approved.");
        MakerCheckerGuard.EnsureDifferent(CreatedBy, approver, "approve a payment proposal");
        if (SubmittedBy is { } s) MakerCheckerGuard.EnsureDifferent(s, approver, "approve a payment proposal");
        Status = ProposalStatus.Approved;
        ApprovedBy = approver;
        ApprovedAt = now;
        ApprovalComment = PaymentRequest.Clip(comment);
    }

    /// <summary>Back to Draft so the maker can change it; the approver must say why.</summary>
    public void SendBack(Guid approver, string reason, DateTimeOffset now)
    {
        Require(Status == ProposalStatus.PendingApproval, "Only a proposal waiting for approval can be sent back.");
        MakerCheckerGuard.EnsureDifferent(CreatedBy, approver, "send back a payment proposal");
        if (string.IsNullOrWhiteSpace(reason)) throw new ValidationFailedException("reason_required", "A reason is required.");
        Status = ProposalStatus.Draft;
        StatusReason = PaymentRequest.Clip(reason);
        SubmittedBy = null;
        SubmittedAt = null;
    }

    public void MarkReleased(Guid releaser, DateTimeOffset now)
    {
        Require(Status == ProposalStatus.Approved, "Only an approved proposal can be released.");
        Status = ProposalStatus.Released;
        ReleasedBy = releaser;
        ReleasedAt = now;
        ClosedAt = now;
    }

    public void Invalidate(string reason, DateTimeOffset now)
    {
        Require(ProposalStatus.Live.Contains(Status), "Only a proposal that was not released can be invalidated.");
        Status = ProposalStatus.Invalidated;
        StatusReason = PaymentRequest.Clip(reason);
        ClosedAt = now;
    }

    public void Cancel(string reason, DateTimeOffset now)
    {
        Require(ProposalStatus.Live.Contains(Status), "Only a proposal that was not released can be cancelled.");
        if (string.IsNullOrWhiteSpace(reason)) throw new ValidationFailedException("reason_required", "A reason is required.");
        Status = ProposalStatus.Cancelled;
        StatusReason = PaymentRequest.Clip(reason);
        ClosedAt = now;
    }

    public void Recalculate() => TotalAmount = Items.Sum(i => i.Amount);

    private void Require(bool condition, string message)
    {
        if (!condition) throw new ConflictException("invalid_transition", message);
    }
}

public class PaymentProposalItem : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid ProposalId { get; set; }
    public Guid PaymentRequestId { get; set; }
    public Guid InvoiceId { get; set; }
    public decimal Amount { get; set; }
}
