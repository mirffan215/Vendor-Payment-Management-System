using System.ComponentModel.DataAnnotations;
using Vpms.BuildingBlocks.Errors;
using Vpms.BuildingBlocks.Tenancy;

namespace Vpms.Payments.Domain;

public static class RequestStatus
{
    public const string Created = "Created";
    public const string InProposal = "InProposal";
    public const string Released = "Released";
    public const string Invalidated = "Invalidated";
    public const string Cancelled = "Cancelled";

    /// <summary>Statuses in which the request still claims its invoice (an invoice has at most one such request).</summary>
    public static readonly IReadOnlyList<string> Open = [Created, InProposal, Released];
}

/// <summary>
/// The intention to pay one approved invoice. It freezes what must still be true at release: the invoice's integrity hash, the verified
/// bank details and the people who took part (so the releaser can be kept away from them).
/// </summary>
public class PaymentRequest : ILegalEntityScoped
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid LegalEntityId { get; set; }
    public Guid VendorId { get; set; }
    public Guid InvoiceId { get; set; }
    public string InvoiceNumber { get; set; } = "";
    public int InvoiceVersion { get; set; }
    public decimal Amount { get; set; }
    public string CurrencyCode { get; set; } = "";
    public DateOnly DueDate { get; set; }

    /// <summary>Hash of the approved invoice when the request was made. Release refuses to go on if it differs.</summary>
    public string InvoiceHash { get; set; } = "";

    public Guid BankDetailsId { get; set; }
    public string BankFingerprint { get; set; } = "";

    /// <summary>Everyone who prepared the invoice, approved it, or made or checked the bank details (comma separated ids).</summary>
    public string InvolvedList { get; set; } = "";

    [System.ComponentModel.DataAnnotations.Schema.NotMapped]
    public IReadOnlyList<Guid> Involved
    {
        get => InvolvedList.Length == 0 ? [] : InvolvedList.Split(',').Select(Guid.Parse).ToList();
        set => InvolvedList = string.Join(',', value.Distinct());
    }

    public string Status { get; set; } = RequestStatus.Created;
    public Guid? ProposalId { get; set; }
    public string? StatusReason { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }

    [Timestamp]
    public byte[] RowVersion { get; set; } = [];

    public void AddToProposal(Guid proposalId)
    {
        Require(Status == RequestStatus.Created, "Only a request that is not yet in a proposal can be added to one.");
        Status = RequestStatus.InProposal;
        ProposalId = proposalId;
    }

    /// <summary>The proposal fell through (cancelled or invalidated because of another item); this request is untouched and can be proposed again.</summary>
    public void ReturnToPool()
    {
        if (Status != RequestStatus.InProposal) return;
        Status = RequestStatus.Created;
        ProposalId = null;
    }

    public void MarkReleased(DateTimeOffset now)
    {
        Require(Status == RequestStatus.InProposal, "Only a request in an approved proposal can be released.");
        Status = RequestStatus.Released;
        ClosedAt = now;
    }

    public void Invalidate(string reason, DateTimeOffset now)
    {
        Require(Status is RequestStatus.Created or RequestStatus.InProposal, "Only an open request that was not released can be invalidated.");
        Status = RequestStatus.Invalidated;
        StatusReason = Clip(reason);
        ClosedAt = now;
    }

    public void Cancel(string reason, DateTimeOffset now)
    {
        Require(Status == RequestStatus.Created, "Only a request that is not in a proposal can be cancelled. Cancel the proposal first.");
        if (string.IsNullOrWhiteSpace(reason)) throw new ValidationFailedException("reason_required", "A reason is required.");
        Status = RequestStatus.Cancelled;
        StatusReason = Clip(reason);
        ClosedAt = now;
    }

    private void Require(bool condition, string message)
    {
        if (!condition) throw new ConflictException("invalid_transition", message);
    }

    internal static string? Clip(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim()[..Math.Min(text.Trim().Length, 500)];
}
