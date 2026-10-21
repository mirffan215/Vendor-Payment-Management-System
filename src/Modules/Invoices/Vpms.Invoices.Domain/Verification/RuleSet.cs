using System.ComponentModel.DataAnnotations;
using Vpms.BuildingBlocks.Errors;
using Vpms.BuildingBlocks.Security;
using Vpms.BuildingBlocks.Tenancy;

namespace Vpms.Invoices.Domain.Verification;

public static class RuleSetStatus
{
    public const string Active = "Active";
    public const string Pending = "Pending";
    public const string Superseded = "Superseded";
    public const string Rejected = "Rejected";
    public const string Cancelled = "Cancelled";
}

/// <summary>
/// One version of a legal entity's verification settings. Versions are immutable; a change creates the next version. A version that
/// makes any rule looser stays Pending until a different person approves it (maker-checker); a tightening change is Active at once.
/// An entity with no version uses the defaults as "version 0".
/// </summary>
public class VerificationRuleSet : ILegalEntityScoped
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid LegalEntityId { get; set; }
    public int VersionNo { get; set; }
    public string ConfigJson { get; set; } = "{}";
    public string Status { get; set; } = RuleSetStatus.Active;
    public string Note { get; set; } = "";

    /// <summary>Rule codes this version loosens compared with the version it was based on (comma separated).</summary>
    public string LoosenedRules { get; set; } = "";

    public Guid CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Guid? DecidedBy { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    public string? DecisionComment { get; set; }

    [Timestamp]
    public byte[] RowVersion { get; set; } = [];

    public bool RequiresApproval => LoosenedRules.Length > 0;

    public void ActivateImmediately(DateTimeOffset now)
    {
        if (RequiresApproval) throw new ConflictException("approval_required", "A change that loosens a rule needs a second person's approval.");
        Status = RuleSetStatus.Active;
        DecidedAt = now;
    }

    public void Approve(Guid checker, string? comment, DateTimeOffset now)
    {
        RequirePending();
        MakerCheckerGuard.EnsureDifferent(CreatedBy, checker, "approve a verification-rule change");
        Status = RuleSetStatus.Active;
        DecidedBy = checker;
        DecidedAt = now;
        DecisionComment = Clean(comment);
    }

    public void Reject(Guid checker, string comment, DateTimeOffset now)
    {
        RequirePending();
        MakerCheckerGuard.EnsureDifferent(CreatedBy, checker, "reject a verification-rule change");
        if (string.IsNullOrWhiteSpace(comment)) throw new ValidationFailedException("reason_required", "A reason is required.");
        Status = RuleSetStatus.Rejected;
        DecidedBy = checker;
        DecidedAt = now;
        DecisionComment = Clean(comment);
    }

    public void Cancel(Guid actor, DateTimeOffset now)
    {
        RequirePending();
        if (actor != CreatedBy) throw new ConflictException("not_the_maker", "Only the person who made the change can cancel it.");
        Status = RuleSetStatus.Cancelled;
        DecidedAt = now;
    }

    public void Supersede() => Status = RuleSetStatus.Superseded;

    private void RequirePending()
    {
        if (Status != RuleSetStatus.Pending) throw new ConflictException("change_not_pending", $"This change is already {Status.ToLowerInvariant()}.");
    }

    private static string? Clean(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim()[..Math.Min(v.Trim().Length, 500)];
}
