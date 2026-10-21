namespace Vpms.Invoices.Domain.Verification;

public static class RuleSeverity
{
    /// <summary>A failing rule stops the invoice: it goes to the exceptions queue and cannot continue until resolved.</summary>
    public const string Block = "Block";

    /// <summary>A failing rule raises an exception for a person to look at, but a person may waive it with a reason.</summary>
    public const string Flag = "Flag";

    public static readonly IReadOnlyList<string> All = [Block, Flag];

    /// <summary>Block is stricter than Flag.</summary>
    public static int Strength(string severity) => severity == Block ? 2 : 1;
}

public static class RuleCodes
{
    public const string DuplicateExact = "duplicate.exact";
    public const string DuplicateSameNumber = "duplicate.same_number";
    public const string DuplicateFuzzy = "duplicate.fuzzy";
    public const string PoRequired = "po.required";
    public const string PoMatch = "po.match";
    public const string AmountTotals = "amount.totals";
    public const string AmountLineMath = "amount.line_math";
    public const string TaxVat = "tax.vat";
    public const string DateSanity = "date.sanity";
    public const string VendorState = "vendor.state";
}

/// <summary>Which way a parameter must move for the rule to catch less, that is, to become looser.</summary>
public enum Loosening
{
    WhenIncreased,
    WhenDecreased,
}

public sealed record ParameterDefinition(string Name, decimal Default, decimal Min, decimal Max, Loosening LoosenedBy);

public sealed record RuleDefinition(
    string Code, bool DefaultEnabled, string DefaultSeverity, bool CanDisable, bool CanFlag, IReadOnlyList<ParameterDefinition> Parameters)
{
    public ParameterDefinition? Parameter(string name) => Parameters.FirstOrDefault(p => p.Name == name);
}

/// <summary>
/// The fixed set of verification rules and what can be configured on each. There is deliberately no expression language:
/// rules are code, their switches and thresholds are per-entity data.
/// </summary>
public static class RuleCatalog
{
    public static readonly IReadOnlyList<RuleDefinition> All =
    [
        new(RuleCodes.DuplicateExact, true, RuleSeverity.Block, CanDisable: true, CanFlag: true, []),
        new(RuleCodes.DuplicateSameNumber, true, RuleSeverity.Flag, true, true, []),
        new(RuleCodes.DuplicateFuzzy, true, RuleSeverity.Flag, true, true,
        [
            new("amountTolerancePercent", 1m, 0m, 10m, Loosening.WhenDecreased),
            new("dateWindowDays", 7m, 0m, 90m, Loosening.WhenDecreased),
            new("numberSimilarity", 0.85m, 0.5m, 1m, Loosening.WhenIncreased),
        ]),
        new(RuleCodes.PoRequired, false, RuleSeverity.Flag, true, true, []),
        new(RuleCodes.PoMatch, true, RuleSeverity.Flag, true, true,
        [
            new("priceTolerancePercent", 2m, 0m, 50m, Loosening.WhenIncreased),
            new("priceToleranceAbsolute", 0m, 0m, 100000m, Loosening.WhenIncreased),
            new("quantityTolerancePercent", 0m, 0m, 50m, Loosening.WhenIncreased),
            new("overBillingPercent", 0m, 0m, 50m, Loosening.WhenIncreased),
        ]),
        new(RuleCodes.AmountTotals, true, RuleSeverity.Block, true, true,
            [new("toleranceMinorUnits", 1m, 0m, 100m, Loosening.WhenIncreased)]),
        new(RuleCodes.AmountLineMath, true, RuleSeverity.Flag, true, true,
            [new("toleranceMinorUnits", 1m, 0m, 100m, Loosening.WhenIncreased)]),
        new(RuleCodes.TaxVat, true, RuleSeverity.Flag, true, true,
            [new("toleranceMinorUnits", 1m, 0m, 100m, Loosening.WhenIncreased)]),
        new(RuleCodes.DateSanity, true, RuleSeverity.Flag, true, true,
        [
            new("maxAgeDays", 365m, 30m, 3650m, Loosening.WhenIncreased),
            new("maxFutureDays", 0m, 0m, 30m, Loosening.WhenIncreased),
        ]),
        // A vendor that may not be transacted with is never waved through.
        new(RuleCodes.VendorState, true, RuleSeverity.Block, CanDisable: false, CanFlag: false, []),
    ];

    public static RuleDefinition? Find(string code) => All.FirstOrDefault(r => r.Code == code);
}
