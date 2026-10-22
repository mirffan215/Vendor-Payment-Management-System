using System.Security.Cryptography;
using System.Text;
using Vpms.BuildingBlocks.Errors;
using Vpms.BuildingBlocks.Security;
using Vpms.BuildingBlocks.Tenancy;

namespace Vpms.ErpIntegration.Domain;

public static class ReconType
{
    public const string MissingInErp = "MissingInErp";
    public const string AmountMismatch = "AmountMismatch";
    public const string VendorMismatch = "VendorMismatch";
    public const string StatusMismatch = "StatusMismatch";
    public const string UnknownInErp = "UnknownInErp";
    public const string PaidInErpNotReleased = "PaidInErpNotReleased";

    public static readonly IReadOnlyList<string> All = [MissingInErp, AmountMismatch, VendorMismatch, StatusMismatch, UnknownInErp, PaidInErpNotReleased];

    /// <summary>Accepting a difference in money or in who is paid is the kind of decision one person must not take alone.</summary>
    public static bool NeedsSecondPerson(string type) => type is AmountMismatch or VendorMismatch;
}

public static class ReconItemStatus
{
    public const string Open = "Open";

    /// <summary>One person proposed to accept the difference; a different person must confirm.</summary>
    public const string PendingAcceptance = "PendingAcceptance";

    public const string Accepted = "Accepted";
    public const string Resolved = "Resolved";

    /// <summary>The next run found the two systems agree again.</summary>
    public const string Cleared = "Cleared";
}

public static class ReconRunStatus
{
    public const string Completed = "Completed";
    public const string Failed = "Failed";
}

/// <summary>One comparison of VPMS with the ERP for one legal entity.</summary>
public class ReconciliationRun : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid LegalEntityId { get; set; }
    public Guid StartedBy { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string Status { get; set; } = ReconRunStatus.Completed;
    public string? ErrorCode { get; set; }
    public int VpmsInvoices { get; set; }
    public int ErpDocuments { get; set; }
    public int NewItems { get; set; }
    public int OpenItems { get; set; }
    public int ClearedItems { get; set; }
}

/// <summary>
/// One difference between VPMS and the ERP. The key names the difference and the two values, so the same difference found by the next run
/// is the same item, and a changed one is a new item. Ending it takes a reason; accepting a money or payee difference takes two people.
/// </summary>
public class ReconciliationItem : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid LegalEntityId { get; set; }
    public string Type { get; set; } = ReconType.MissingInErp;
    public string ItemKey { get; set; } = "";
    public Guid? InvoiceId { get; set; }
    public string? InvoiceNumber { get; set; }
    public string? ErpReference { get; set; }
    public string VpmsValue { get; set; } = "";
    public string ErpValue { get; set; } = "";
    public string Status { get; set; } = ReconItemStatus.Open;
    public Guid FirstSeenRunId { get; set; }
    public Guid LastSeenRunId { get; set; }
    public DateTimeOffset FirstSeenAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
    public Guid? ProposedBy { get; set; }
    public string? ProposedReason { get; set; }
    public Guid? DecidedBy { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    public string? DecisionReason { get; set; }

    [System.ComponentModel.DataAnnotations.Timestamp]
    public byte[] RowVersion { get; set; } = [];

    public static string KeyFor(string type, string subject, string vpmsValue, string erpValue) =>
        $"{type}|{subject}|{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(vpmsValue + "␟" + erpValue)))[..12].ToLowerInvariant()}";

    public bool IsOpenForDecision => Status is ReconItemStatus.Open or ReconItemStatus.PendingAcceptance;

    /// <summary>A later run found the same difference again.</summary>
    public void Seen(Guid runId, DateTimeOffset now)
    {
        LastSeenRunId = runId;
        LastSeenAt = now;
    }

    /// <summary>The difference was dealt with (corrected in one of the systems, or explained).</summary>
    public void Resolve(Guid actor, string reason, DateTimeOffset now)
    {
        RequireOpen();
        Decide(actor, reason, now, ReconItemStatus.Resolved);
    }

    /// <summary>
    /// Asks to leave the difference as it is. For a money or payee difference this only proposes it; a different person must confirm.
    /// </summary>
    public void ProposeAcceptance(Guid actor, string reason, DateTimeOffset now)
    {
        RequireOpen();
        if (Status != ReconItemStatus.Open) throw new ConflictException("already_proposed", "Acceptance has already been proposed.");
        if (!ReconType.NeedsSecondPerson(Type))
        {
            Decide(actor, reason, now, ReconItemStatus.Accepted);
            return;
        }

        var text = Required(reason);
        ProposedBy = actor;
        ProposedReason = text;
        Status = ReconItemStatus.PendingAcceptance;
    }

    public void ApproveAcceptance(Guid checker, string? comment, DateTimeOffset now)
    {
        if (Status != ReconItemStatus.PendingAcceptance) throw new ConflictException("not_pending", "No acceptance is waiting for approval.");
        MakerCheckerGuard.EnsureDifferent(ProposedBy!.Value, checker, "approve accepting a reconciliation difference");
        Status = ReconItemStatus.Accepted;
        DecidedBy = checker;
        DecidedAt = now;
        DecisionReason = string.IsNullOrWhiteSpace(comment) ? ProposedReason : Cut(comment);
    }

    public void RejectAcceptance(Guid checker, string reason, DateTimeOffset now)
    {
        if (Status != ReconItemStatus.PendingAcceptance) throw new ConflictException("not_pending", "No acceptance is waiting for approval.");
        MakerCheckerGuard.EnsureDifferent(ProposedBy!.Value, checker, "reject accepting a reconciliation difference");
        var text = Required(reason);
        Status = ReconItemStatus.Open;
        DecisionReason = text;
        DecidedBy = checker;
        DecidedAt = now;
        ProposedBy = null;
        ProposedReason = null;
    }

    /// <summary>A run found the two systems agree again.</summary>
    public void Clear(DateTimeOffset now)
    {
        if (!IsOpenForDecision) return;
        Status = ReconItemStatus.Cleared;
        DecidedAt = now;
        DecisionReason = "The systems agree again.";
    }

    private void Decide(Guid actor, string reason, DateTimeOffset now, string status)
    {
        var text = Required(reason);
        Status = status;
        DecidedBy = actor;
        DecidedAt = now;
        DecisionReason = text;
    }

    private void RequireOpen()
    {
        if (!IsOpenForDecision) throw new ConflictException("invalid_transition", "This difference has already been dealt with.");
    }

    private static string Required(string reason) =>
        string.IsNullOrWhiteSpace(reason) ? throw new ValidationFailedException("reason_required", "A reason is required.") : Cut(reason);

    private static string Cut(string text) => text.Trim()[..Math.Min(text.Trim().Length, 500)];
}
