using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Vpms.BuildingBlocks.Errors;
using Vpms.BuildingBlocks.Jobs;
using Vpms.BuildingBlocks.Security;
using Vpms.BuildingBlocks.Tenancy;

namespace Vpms.CustomerAdmin.Domain;

public sealed record RetentionIssue(string Code, string Message);

/// <summary>Months to keep each class, for the whole tenant and optionally per country of the legal entity (the country wins).</summary>
public sealed record RetentionSettings(IReadOnlyDictionary<string, int> Months, IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>> Countries)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static RetentionSettings Default { get; } = new(new Dictionary<string, int>(), new Dictionary<string, IReadOnlyDictionary<string, int>>());

    /// <summary>The months that apply: the country's setting, else the tenant's, else the system default.</summary>
    public int MonthsFor(string dataClass, string? countryCode)
    {
        if (countryCode is not null && Countries.TryGetValue(countryCode.ToUpperInvariant(), out var country) && country.TryGetValue(dataClass, out var c)) return c;
        return Months.TryGetValue(dataClass, out var m) ? m : RetentionDefaults.Months[dataClass];
    }

    public IReadOnlyList<RetentionIssue> Validate()
    {
        var issues = new List<RetentionIssue>();
        void Check(string scope, IReadOnlyDictionary<string, int> months)
        {
            foreach (var (cls, value) in months)
            {
                if (!RetentionClasses.All.Contains(cls)) issues.Add(new("retention_class_unknown", $"Unknown data class '{cls}'."));
                else if (value is < 1 or > 1200) issues.Add(new("retention_months_invalid", $"{scope}: {cls} must be between 1 and 1200 months."));
            }
        }

        Check("Tenant", Months);
        foreach (var (country, months) in Countries)
        {
            if (country.Length != 2 || !country.All(char.IsAsciiLetterUpper)) issues.Add(new("retention_country_invalid", $"'{country}' is not a two-letter country code."));
            Check(country, months);
        }

        // The audit trail must outlive the records it describes, in every scope.
        foreach (var country in Countries.Keys.Prepend(null))
        {
            if (country is not null && country.Length != 2) continue;
            if (MonthsFor(RetentionClasses.Audit, country) < MonthsFor(RetentionClasses.FinancialRecords, country))
            {
                issues.Add(new("retention_audit_shorter", "The audit trail must be kept at least as long as the financial records."));
                break;
            }
        }

        return issues;
    }

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static RetentionSettings Parse(string json) => JsonSerializer.Deserialize<RetentionSettings>(json, Json) ?? Default;
}

public static class RetentionStatus
{
    public const string Active = "Active";
    public const string Pending = "Pending";
    public const string Superseded = "Superseded";
    public const string Rejected = "Rejected";
    public const string Cancelled = "Cancelled";
}

/// <summary>One version of the tenant's retention policy. Shortening what is kept is deleting; every version waits for a different person to approve it.</summary>
public class RetentionPolicyVersion : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public int VersionNo { get; set; }
    public string SettingsJson { get; set; } = "{}";
    public string Status { get; set; } = RetentionStatus.Pending;
    public string Note { get; set; } = "";
    public Guid CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Guid? DecidedBy { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    public string? DecisionComment { get; set; }

    [Timestamp]
    public byte[] RowVersion { get; set; } = [];

    public void Approve(Guid checker, string? comment, DateTimeOffset now)
    {
        RequirePending();
        MakerCheckerGuard.EnsureDifferent(CreatedBy, checker, "approve a retention policy change");
        Status = RetentionStatus.Active;
        DecidedBy = checker;
        DecidedAt = now;
        DecisionComment = Clean(comment);
    }

    public void Reject(Guid checker, string comment, DateTimeOffset now)
    {
        RequirePending();
        MakerCheckerGuard.EnsureDifferent(CreatedBy, checker, "reject a retention policy change");
        if (string.IsNullOrWhiteSpace(comment)) throw new ValidationFailedException("reason_required", "A reason is required.");
        Status = RetentionStatus.Rejected;
        DecidedBy = checker;
        DecidedAt = now;
        DecisionComment = Clean(comment);
    }

    public void Cancel(Guid actor, DateTimeOffset now)
    {
        RequirePending();
        if (actor != CreatedBy) throw new ConflictException("not_the_maker", "Only the person who made the change can cancel it.");
        Status = RetentionStatus.Cancelled;
        DecidedAt = now;
    }

    public void Supersede()
    {
        if (Status != RetentionStatus.Active) throw new ConflictException("invalid_transition", "Only the active version can be superseded.");
        Status = RetentionStatus.Superseded;
    }

    private void RequirePending()
    {
        if (Status != RetentionStatus.Pending) throw new ConflictException("not_pending", "This change has already been decided.");
    }

    private static string? Clean(string? comment) => string.IsNullOrWhiteSpace(comment) ? null : comment.Trim()[..Math.Min(comment.Trim().Length, 500)];
}

public static class HoldScope
{
    public const string Tenant = "Tenant";
    public const string LegalEntity = "LegalEntity";
    public const string Vendor = "Vendor";
}

/// <summary>Stops disposal of the records it covers (litigation, an investigation, an audit). Placing is quick; releasing needs a different person.</summary>
public class LegalHold : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string Scope { get; set; } = HoldScope.Tenant;
    public Guid? LegalEntityId { get; set; }
    public Guid? VendorId { get; set; }
    public string Reason { get; set; } = "";
    public Guid PlacedBy { get; set; }
    public DateTimeOffset PlacedAt { get; set; }
    public Guid? ReleasedBy { get; set; }
    public DateTimeOffset? ReleasedAt { get; set; }
    public string? ReleaseComment { get; set; }

    [Timestamp]
    public byte[] RowVersion { get; set; } = [];

    public bool IsActive => ReleasedAt is null;

    public static LegalHold Place(string scope, Guid? legalEntityId, Guid? vendorId, string reason, Guid actor, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length > 500) throw new ValidationFailedException("hold_reason_required", "Give the reason for the hold (up to 500 characters).");
        var valid = (scope, legalEntityId, vendorId) switch
        {
            (HoldScope.Tenant, null, null) => true,
            (HoldScope.LegalEntity, not null, null) => true,
            (HoldScope.Vendor, null, not null) => true,
            _ => false,
        };
        if (!valid) throw new ValidationFailedException("hold_scope_invalid", "The hold scope and the entity or vendor it names do not match.");
        return new LegalHold { Id = Guid.NewGuid(), Scope = scope, LegalEntityId = legalEntityId, VendorId = vendorId, Reason = reason.Trim(), PlacedBy = actor, PlacedAt = now };
    }

    public void Release(Guid actor, string comment, DateTimeOffset now)
    {
        if (!IsActive) throw new ConflictException("hold_already_released", "This hold has already been released.");
        MakerCheckerGuard.EnsureDifferent(PlacedBy, actor, "release a legal hold");
        if (string.IsNullOrWhiteSpace(comment)) throw new ValidationFailedException("reason_required", "A reason is required.");
        ReleasedBy = actor;
        ReleasedAt = now;
        ReleaseComment = comment.Trim()[..Math.Min(comment.Trim().Length, 500)];
    }
}
