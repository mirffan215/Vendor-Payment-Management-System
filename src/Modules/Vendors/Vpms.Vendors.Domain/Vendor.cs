using System.ComponentModel.DataAnnotations;
using Vpms.BuildingBlocks.Errors;
using Vpms.BuildingBlocks.Security;
using Vpms.BuildingBlocks.Tenancy;

namespace Vpms.Vendors.Domain;

public static class VendorStatus
{
    public const string Invited = "Invited";
    public const string Submitted = "Submitted";
    public const string UnderReview = "UnderReview";
    public const string Active = "Active";
    public const string Rejected = "Rejected";
    public const string Suspended = "Suspended";
}

public static class VerificationStatus
{
    public const string NotStarted = "NotStarted";
    public const string Pending = "Pending";
    public const string InReview = "InReview";
    public const string Verified = "Verified";
    public const string Rejected = "Rejected";
}

public static class ActorKind
{
    public const string Staff = "Staff";
    public const string Vendor = "Vendor";
}

/// <summary>
/// A vendor of the tenant. All status changes go through the methods below, which enforce the allowed transitions and the
/// maker-checker rule: whoever created or edited the vendor data (or submitted it as staff) cannot also approve it.
/// </summary>
public class Vendor : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string LegalName { get; set; } = "";
    public string? LegalNameAr { get; set; }
    public string? TradeName { get; set; }
    public string CountryCode { get; set; } = "";
    public string? TaxIdCipher { get; set; }
    public string? TaxIdIndex { get; set; }
    public string? RegistrationNoCipher { get; set; }
    public string? ContactEmail { get; set; }
    public string? ContactPhone { get; set; }
    public string Status { get; set; } = VendorStatus.Invited;
    public string VerificationStatus { get; set; } = Domain.VerificationStatus.NotStarted;
    public string? StatusReason { get; set; }

    public Guid CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Guid? LastEditedBy { get; set; }
    public DateTimeOffset? LastEditedAt { get; set; }
    public Guid? SubmittedBy { get; set; }
    public string? SubmittedByKind { get; set; }
    public DateTimeOffset? SubmittedAt { get; set; }
    public Guid? ReviewedBy { get; set; }
    public Guid? ApprovedBy { get; set; }
    public DateTimeOffset? ApprovedAt { get; set; }
    public Guid? SuspendedBy { get; set; }

    [Timestamp]
    public byte[] RowVersion { get; set; } = [];

    public void MarkEdited(Guid staffId, DateTimeOffset now)
    {
        LastEditedBy = staffId;
        LastEditedAt = now;
    }

    public void Submit(Guid? actorId, string actorKind, DateTimeOffset now)
    {
        Require(Status == VendorStatus.Invited, "Only a vendor that has not been submitted yet can be submitted.");
        Status = VendorStatus.Submitted;
        VerificationStatus = Domain.VerificationStatus.Pending;
        SubmittedBy = actorId;
        SubmittedByKind = actorKind;
        SubmittedAt = now;
        StatusReason = null;
    }

    public void StartReview(Guid reviewer)
    {
        Require(Status == VendorStatus.Submitted, "Only a submitted vendor can be taken into review.");
        Status = VendorStatus.UnderReview;
        VerificationStatus = Domain.VerificationStatus.InReview;
        ReviewedBy = reviewer;
    }

    public void Approve(Guid approver, DateTimeOffset now)
    {
        Require(Status == VendorStatus.UnderReview, "Only a vendor under review can be approved.");

        var makers = new List<PriorActor> { new("creator of the vendor", CreatedBy) };
        if (LastEditedBy is { } editor) makers.Add(new("person who last edited the vendor", editor));
        if (SubmittedBy is { } submitter && SubmittedByKind == ActorKind.Staff) makers.Add(new("person who submitted the vendor", submitter));
        DifferentPersonRule.Ensure(approver, "approve this vendor", makers);

        Status = VendorStatus.Active;
        VerificationStatus = Domain.VerificationStatus.Verified;
        ApprovedBy = approver;
        ApprovedAt = now;
        StatusReason = null;
    }

    public void Reject(string reason, DateTimeOffset now)
    {
        Require(Status is VendorStatus.Submitted or VendorStatus.UnderReview, "Only a submitted vendor can be rejected.");
        RequireReason(reason);
        Status = VendorStatus.Rejected;
        VerificationStatus = Domain.VerificationStatus.Rejected;
        StatusReason = reason.Trim();
    }

    /// <summary>A rejected vendor goes back to the start so the vendor can correct and resubmit.</summary>
    public void Reopen()
    {
        Require(Status == VendorStatus.Rejected, "Only a rejected vendor can be reopened.");
        Status = VendorStatus.Invited;
        VerificationStatus = Domain.VerificationStatus.NotStarted;
        SubmittedBy = null;
        SubmittedByKind = null;
        SubmittedAt = null;
        ReviewedBy = null;
    }

    public void Suspend(Guid actor, string reason)
    {
        Require(Status == VendorStatus.Active, "Only an active vendor can be suspended.");
        RequireReason(reason);
        Status = VendorStatus.Suspended;
        SuspendedBy = actor;
        StatusReason = reason.Trim();
    }

    public void Reactivate(Guid actor)
    {
        Require(Status == VendorStatus.Suspended, "Only a suspended vendor can be reactivated.");
        // The person who suspended a vendor cannot lift the suspension alone.
        if (SuspendedBy is { } suspender)
        {
            DifferentPersonRule.Ensure(actor, "reactivate this vendor", [new("person who suspended the vendor", suspender)]);
        }

        Status = VendorStatus.Active;
        SuspendedBy = null;
        StatusReason = null;
    }

    public bool CanTransact => Status == VendorStatus.Active;

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new ConflictException("invalid_transition", message);
        }
    }

    private static void RequireReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 500)
        {
            throw new ValidationFailedException("reason_required", "A reason is required (max 500 characters).");
        }
    }
}
