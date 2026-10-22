using Vpms.BuildingBlocks.Errors;
using Vpms.BuildingBlocks.Tenancy;

namespace Vpms.Workflow.Domain;

public static class DelegationStatus
{
    public const string Active = "Active";
    public const string Revoked = "Revoked";
}

/// <summary>
/// An approver lets a substitute decide tasks assigned to them, for one legal entity and a date range. The delegate acts with the
/// delegator's authority, never more, and never on an invoice the delegate prepared. A delegate cannot pass the delegation on.
/// </summary>
public class Delegation : ILegalEntityScoped
{
    public const int MaxDays = 180;

    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid LegalEntityId { get; set; }
    public Guid DelegatorId { get; set; }
    public Guid DelegateId { get; set; }
    public DateTimeOffset ValidFrom { get; set; }
    public DateTimeOffset ValidTo { get; set; }

    /// <summary>Optional cap: the delegate may not approve invoices above this amount (in addition to the delegator's own limit).</summary>
    public decimal? MaxAmount { get; set; }

    public string Status { get; set; } = DelegationStatus.Active;
    public string Reason { get; set; } = "";
    public Guid CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Guid? RevokedBy { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }

    [System.ComponentModel.DataAnnotations.Timestamp]
    public byte[] RowVersion { get; set; } = [];

    public bool CoversAt(DateTimeOffset at) => Status == DelegationStatus.Active && ValidFrom <= at && at < ValidTo;

    public static void ValidateWindow(Guid delegator, Guid delegateId, DateTimeOffset from, DateTimeOffset to, decimal? cap, string? reason)
    {
        if (delegator == delegateId) throw new ValidationFailedException("self_delegation", "You cannot delegate to yourself.");
        if (to <= from) throw new ValidationFailedException("invalid_period", "The delegation must end after it starts.");
        if (to - from > TimeSpan.FromDays(MaxDays)) throw new ValidationFailedException("period_too_long", $"A delegation can last at most {MaxDays} days.");
        if (cap is <= 0) throw new ValidationFailedException("invalid_cap", "The amount cap must be positive.");
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 300) throw new ValidationFailedException("reason_required", "Say why (max 300 characters).");
    }

    public void Revoke(Guid by, DateTimeOffset now)
    {
        if (Status != DelegationStatus.Active) throw new ConflictException("not_active", "This delegation has already been revoked.");
        Status = DelegationStatus.Revoked;
        RevokedBy = by;
        RevokedAt = now;
    }
}

public static class NoticeKind
{
    public const string Reminder = "Reminder";
    public const string Escalated = "Escalated";
    public const string EscalatedAway = "EscalatedAway";
    public const string Stalled = "Stalled";
}

/// <summary>An in-app message for one person. Email and chat delivery are added by the notifications phase.</summary>
public class WorkflowNotice : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid UserId { get; set; }
    public string Kind { get; set; } = NoticeKind.Reminder;
    public Guid RunId { get; set; }
    public Guid TaskId { get; set; }
    public string SubjectLabel { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ReadAt { get; set; }
}
