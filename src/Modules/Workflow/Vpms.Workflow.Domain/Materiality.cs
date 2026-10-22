using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text.Json;
using Vpms.BuildingBlocks.Errors;
using Vpms.BuildingBlocks.Security;
using Vpms.BuildingBlocks.Tenancy;

namespace Vpms.Workflow.Domain;

/// <summary>
/// When is a change to an approved (or in-approval) subject big enough to need approval again? Money fields use the thresholds
/// (a change counts when it is larger than both the absolute and the percentage allowance); every other field in
/// <see cref="MaterialFields"/> counts on any change. The defaults treat every change as material.
/// </summary>
public sealed record MaterialityConfig(decimal AmountPercent, decimal AmountAbsolute, IReadOnlyList<string> MaterialFields)
{
    public static readonly IReadOnlyList<string> MoneyFields = ["total", "taxTotal"];
    public static readonly IReadOnlyList<string> TextFields = ["currency", "vendor", "number", "date", "dueDate", "poReference", "supplierTaxNumber", "lineCount", "dimensions"];
    public static IReadOnlyList<string> AllFields => [.. MoneyFields, .. TextFields];

    public static MaterialityConfig Defaults() => new(0m, 0m, AllFields);

    public IReadOnlyList<DefinitionError> Validate()
    {
        var errors = new List<DefinitionError>();
        if (AmountPercent is < 0 or > 100) errors.Add(new DefinitionError("percent_invalid", "The percentage must be between 0 and 100."));
        if (AmountAbsolute < 0) errors.Add(new DefinitionError("absolute_invalid", "The amount allowance cannot be negative."));
        foreach (var f in MaterialFields.Where(f => !AllFields.Contains(f))) errors.Add(new DefinitionError("field_unknown", $"Unknown field '{f}'."));
        if (MaterialFields.Distinct().Count() != MaterialFields.Count) errors.Add(new DefinitionError("field_duplicate", "A field is listed twice."));
        return errors;
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static MaterialityConfig Parse(string json) => JsonSerializer.Deserialize<MaterialityConfig>(json, Json) ?? Defaults();
}

public sealed record MaterialityOutcome(bool Material, IReadOnlyList<string> ChangedFields);

public static class Materiality
{
    /// <summary>Compares snapshots of the subject as approved and as now proposed. Fields missing from a snapshot count as empty.</summary>
    public static MaterialityOutcome Evaluate(MaterialityConfig config, IReadOnlyDictionary<string, string> baseline, IReadOnlyDictionary<string, string> current)
    {
        var changed = new List<string>();
        foreach (var field in config.MaterialFields)
        {
            var before = baseline.GetValueOrDefault(field, "");
            var after = current.GetValueOrDefault(field, "");
            if (MaterialityConfig.MoneyFields.Contains(field))
            {
                var b = Number(before);
                var a = Number(after);
                var allowance = Math.Max(config.AmountAbsolute, Math.Abs(b) * config.AmountPercent / 100m);
                if (Math.Abs(a - b) > allowance) changed.Add(field);
            }
            else if (!string.Equals(before.Trim(), after.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                changed.Add(field);
            }
        }

        return new MaterialityOutcome(changed.Count > 0, changed);
    }

    private static decimal Number(string s) => decimal.TryParse(s, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var d) ? d : 0m;
}

/// <summary>One version of a legal entity's materiality settings. Like workflow versions, every change waits for a different approver.</summary>
public class MaterialityPolicy : ILegalEntityScoped
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid LegalEntityId { get; set; }
    public string SubjectType { get; set; } = SubjectTypes.Invoice;
    public int VersionNo { get; set; }
    public string ConfigJson { get; set; } = "{}";
    public string Status { get; set; } = DefinitionStatus.Pending;
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
        MakerCheckerGuard.EnsureDifferent(CreatedBy, checker, "approve a materiality change");
        Status = DefinitionStatus.Active;
        DecidedBy = checker;
        DecidedAt = now;
        DecisionComment = Clean(comment);
    }

    public void Reject(Guid checker, string comment, DateTimeOffset now)
    {
        RequirePending();
        MakerCheckerGuard.EnsureDifferent(CreatedBy, checker, "reject a materiality change");
        if (string.IsNullOrWhiteSpace(comment)) throw new ValidationFailedException("reason_required", "A reason is required.");
        Status = DefinitionStatus.Rejected;
        DecidedBy = checker;
        DecidedAt = now;
        DecisionComment = Clean(comment);
    }

    public void Cancel(Guid actor, DateTimeOffset now)
    {
        RequirePending();
        if (actor != CreatedBy) throw new ConflictException("not_the_maker", "Only the person who made the change can cancel it.");
        Status = DefinitionStatus.Cancelled;
        DecidedAt = now;
    }

    public void Supersede()
    {
        if (Status != DefinitionStatus.Active) throw new ConflictException("invalid_transition", "Only the active version can be superseded.");
        Status = DefinitionStatus.Superseded;
    }

    private void RequirePending()
    {
        if (Status != DefinitionStatus.Pending) throw new ConflictException("not_pending", "This change has already been decided.");
    }

    private static string? Clean(string? comment) => string.IsNullOrWhiteSpace(comment) ? null : comment.Trim()[..Math.Min(comment.Trim().Length, 500)];
}
