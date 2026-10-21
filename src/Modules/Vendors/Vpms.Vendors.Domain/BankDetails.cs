using System.ComponentModel.DataAnnotations;
using Vpms.BuildingBlocks.Errors;
using Vpms.BuildingBlocks.Security;
using Vpms.BuildingBlocks.Tenancy;

namespace Vpms.Vendors.Domain;

public static class BankDetailsStatus
{
    public const string Pending = "Pending";
    public const string Verified = "Verified";
    public const string Superseded = "Superseded";
    public const string Revoked = "Revoked";
}

public static class ChangeStatus
{
    public const string Pending = "Pending";
    public const string Approved = "Approved";
    public const string Rejected = "Rejected";
    public const string Cancelled = "Cancelled";
    public const string Expired = "Expired";

    public static readonly IReadOnlyList<string> Terminal = [Approved, Rejected, Cancelled, Expired];
}

public static class ChangeSource
{
    public const string Staff = "Staff";
    public const string VendorPortal = "VendorPortal";
}

/// <summary>
/// One set of bank details of a vendor. Values are never edited: a change creates a new Pending row. A row only becomes
/// Verified through an approved <see cref="BankDetailChange"/> (enforced by the database as well).
/// </summary>
public class BankDetails : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid VendorId { get; set; }
    public string AccountName { get; set; } = "";
    public string BankName { get; set; } = "";
    public string? SwiftBic { get; set; }
    public string CountryCode { get; set; } = "";
    public string CurrencyCode { get; set; } = "";
    public string? IbanCipher { get; set; }
    public string? IbanIndex { get; set; }
    public string? AccountNumberCipher { get; set; }
    public string? AccountNumberIndex { get; set; }

    /// <summary>Only the tail of the account identifier, for lists and screens. The full value stays encrypted.</summary>
    public string MaskedAccount { get; set; } = "";

    public string Status { get; set; } = BankDetailsStatus.Pending;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? VerifiedAt { get; set; }

    [Timestamp]
    public byte[] RowVersion { get; set; } = [];
}

/// <summary>
/// Request to put new bank details into effect. The maker proposes; a different person with approval rights decides. For
/// changes to an existing account, and for changes raised by the vendor, the decision must reference an out-of-band
/// callback verification with a known vendor contact.
/// </summary>
public class BankDetailChange : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid VendorId { get; set; }
    public Guid ProposedBankDetailsId { get; set; }
    public Guid? ReplacesBankDetailsId { get; set; }
    public string Reason { get; set; } = "";
    public string Source { get; set; } = ChangeSource.Staff;
    public Guid MadeBy { get; set; }
    public DateTimeOffset MadeAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public string Status { get; set; } = ChangeStatus.Pending;
    public Guid? CheckedBy { get; set; }
    public DateTimeOffset? CheckedAt { get; set; }
    public string? CheckerComment { get; set; }
    public string? CallbackVerificationRef { get; set; }

    [Timestamp]
    public byte[] RowVersion { get; set; } = [];

    public bool RequiresCallback => ReplacesBankDetailsId is not null || Source == ChangeSource.VendorPortal;

    public void Approve(Guid checker, string? callbackRef, string? comment, DateTimeOffset now)
    {
        RequirePending(now);
        MakerCheckerGuard.EnsureDifferent(MadeBy, checker, "approve a bank-detail change");

        if (RequiresCallback && string.IsNullOrWhiteSpace(callbackRef))
        {
            throw new ValidationFailedException("callback_required",
                "Confirm the change with the vendor by phone on a number already on file, and record the reference.");
        }

        Status = ChangeStatus.Approved;
        CheckedBy = checker;
        CheckedAt = now;
        CheckerComment = Clean(comment);
        CallbackVerificationRef = Clean(callbackRef);
    }

    public void Reject(Guid checker, string comment, DateTimeOffset now)
    {
        RequirePending(now);
        MakerCheckerGuard.EnsureDifferent(MadeBy, checker, "reject a bank-detail change");
        if (string.IsNullOrWhiteSpace(comment))
        {
            throw new ValidationFailedException("reason_required", "A reason is required.");
        }

        Status = ChangeStatus.Rejected;
        CheckedBy = checker;
        CheckedAt = now;
        CheckerComment = Clean(comment);
    }

    /// <summary>Only the person who made the request can withdraw it.</summary>
    public void Cancel(Guid actor, DateTimeOffset now)
    {
        RequirePending(now);
        if (actor != MadeBy)
        {
            throw new ConflictException("not_the_maker", "Only the person who made the request can cancel it.");
        }

        Status = ChangeStatus.Cancelled;
    }

    public bool IsStale(DateTimeOffset now) => Status == ChangeStatus.Pending && ExpiresAt <= now;

    public void Expire()
    {
        if (Status == ChangeStatus.Pending)
        {
            Status = ChangeStatus.Expired;
        }
    }

    private void RequirePending(DateTimeOffset now)
    {
        if (Status != ChangeStatus.Pending)
        {
            throw new ConflictException("change_not_pending", $"This change is already {Status.ToLowerInvariant()}.");
        }

        if (ExpiresAt <= now)
        {
            throw new ConflictException("change_expired", "This change request has expired. Start a new one.");
        }
    }

    private static string? Clean(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim()[..Math.Min(v.Trim().Length, 500)];
}
