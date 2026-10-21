using Vpms.BuildingBlocks.Errors;
using Vpms.BuildingBlocks.Security;
using Vpms.Invoices.Domain.Verification;

namespace Vpms.UnitTests.Invoices;

public class RuleCatalogTests
{
    [Fact]
    public void Every_rule_code_is_unique_and_has_a_default_inside_its_own_limits()
    {
        Assert.Equal(RuleCatalog.All.Count, RuleCatalog.All.Select(r => r.Code).Distinct().Count());
        foreach (var rule in RuleCatalog.All)
        {
            Assert.Contains(rule.DefaultSeverity, RuleSeverity.All);
            foreach (var p in rule.Parameters)
            {
                Assert.InRange(p.Default, p.Min, p.Max);
                Assert.True(p.Min < p.Max, $"{rule.Code}.{p.Name}");
            }
        }
    }

    [Fact]
    public void The_vendor_state_rule_always_blocks()
    {
        var rule = RuleCatalog.Find(RuleCodes.VendorState)!;
        Assert.False(rule.CanDisable);
        Assert.False(rule.CanFlag);
        Assert.Equal(RuleSeverity.Block, rule.DefaultSeverity);
    }

    [Fact]
    public void Exact_duplicates_block_by_default_and_similar_ones_only_flag()
    {
        var d = RuleConfig.Defaults();
        Assert.Equal(RuleSeverity.Block, d.Get(RuleCodes.DuplicateExact).Severity);
        Assert.Equal(RuleSeverity.Flag, d.Get(RuleCodes.DuplicateSameNumber).Severity);
        Assert.Equal(RuleSeverity.Flag, d.Get(RuleCodes.DuplicateFuzzy).Severity);
        Assert.Equal(RuleSeverity.Block, d.Get(RuleCodes.AmountTotals).Severity);
        Assert.False(d.Get(RuleCodes.PoRequired).Enabled);
    }
}

public class RuleConfigNormalizeTests
{
    private static Dictionary<string, RuleSetting> From(RuleConfig c) => new(c.Rules);

    private static string Code(IReadOnlyDictionary<string, RuleSetting> rules) => Assert.Throws<ValidationFailedException>(() => RuleConfig.Normalize(rules)).Code;

    [Fact]
    public void Defaults_survive_normalisation_unchanged()
    {
        var normalized = RuleConfig.Normalize(From(RuleConfig.Defaults()));
        Assert.Equal(RuleConfig.Defaults().Rules.Keys, normalized.Rules.Keys);
        Assert.Equal(1m, normalized.Param(RuleCodes.AmountTotals, "toleranceMinorUnits"));
    }

    [Fact]
    public void Missing_rules_and_parameters_get_their_defaults()
    {
        var normalized = RuleConfig.Normalize(new Dictionary<string, RuleSetting>
        {
            [RuleCodes.PoMatch] = new(true, RuleSeverity.Block, new Dictionary<string, decimal> { ["priceTolerancePercent"] = 5m }),
        });
        Assert.Equal(RuleSeverity.Block, normalized.Get(RuleCodes.PoMatch).Severity);
        Assert.Equal(5m, normalized.Param(RuleCodes.PoMatch, "priceTolerancePercent"));
        Assert.Equal(0m, normalized.Param(RuleCodes.PoMatch, "quantityTolerancePercent"));
        Assert.Equal(RuleConfig.Defaults().Rules.Count, normalized.Rules.Count);
        Assert.Equal(RuleSeverity.Block, normalized.Get(RuleCodes.DuplicateExact).Severity);
    }

    [Fact]
    public void A_null_submission_means_all_defaults() => Assert.Equal(RuleConfig.Defaults().Rules.Count, RuleConfig.Normalize(null).Rules.Count);

    [Fact]
    public void Unknown_rules_and_parameters_are_refused()
    {
        Assert.Equal("unknown_rule", Code(new Dictionary<string, RuleSetting> { ["made.up"] = new(true, "Flag", new Dictionary<string, decimal>()) }));
        Assert.Equal("unknown_parameter", Code(new Dictionary<string, RuleSetting>
        {
            [RuleCodes.PoMatch] = new(true, "Flag", new Dictionary<string, decimal> { ["nonsense"] = 1m }),
        }));
    }

    [Theory]
    [InlineData("block")]
    [InlineData("Warn")]
    [InlineData("")]
    public void Severity_must_be_block_or_flag(string severity) =>
        Assert.Equal("invalid_severity", Code(new Dictionary<string, RuleSetting> { [RuleCodes.PoMatch] = new(true, severity, new Dictionary<string, decimal>()) }));

    [Fact]
    public void The_vendor_state_rule_cannot_be_switched_off_or_lowered()
    {
        Assert.Equal("rule_not_disableable", Code(new Dictionary<string, RuleSetting> { [RuleCodes.VendorState] = new(false, "Block", new Dictionary<string, decimal>()) }));
        Assert.Equal("invalid_severity", Code(new Dictionary<string, RuleSetting> { [RuleCodes.VendorState] = new(true, "Flag", new Dictionary<string, decimal>()) }));
    }

    [Theory]
    [InlineData(RuleCodes.PoMatch, "priceTolerancePercent", -0.1)]
    [InlineData(RuleCodes.PoMatch, "priceTolerancePercent", 50.1)]
    [InlineData(RuleCodes.DuplicateFuzzy, "numberSimilarity", 0.49)]
    [InlineData(RuleCodes.DuplicateFuzzy, "numberSimilarity", 1.01)]
    [InlineData(RuleCodes.DateSanity, "maxAgeDays", 29)]
    [InlineData(RuleCodes.AmountTotals, "toleranceMinorUnits", 101)]
    public void Parameters_outside_their_limits_are_refused(string rule, string parameter, double value) =>
        Assert.Equal("parameter_out_of_range", Code(new Dictionary<string, RuleSetting>
        {
            [rule] = new(true, "Flag", new Dictionary<string, decimal> { [parameter] = (decimal)value }),
        }));

    [Theory]
    [InlineData(RuleCodes.PoMatch, "priceTolerancePercent", 0)]
    [InlineData(RuleCodes.PoMatch, "priceTolerancePercent", 50)]
    [InlineData(RuleCodes.DuplicateFuzzy, "numberSimilarity", 0.5)]
    [InlineData(RuleCodes.DuplicateFuzzy, "numberSimilarity", 1)]
    public void Limits_are_inclusive(string rule, string parameter, double value) =>
        RuleConfig.Normalize(new Dictionary<string, RuleSetting> { [rule] = new(true, "Flag", new Dictionary<string, decimal> { [parameter] = (decimal)value }) });
}

public class RuleLooseningTests
{
    private static RuleConfig With(string rule, bool enabled, string severity, params (string Name, decimal Value)[] parameters) =>
        RuleConfig.Normalize(new Dictionary<string, RuleSetting> { [rule] = new(enabled, severity, parameters.ToDictionary(p => p.Name, p => p.Value)) });

    private static IReadOnlyList<string> Loosened(RuleConfig from, RuleConfig to) => RuleConfig.LoosenedRules(from, to);

    [Fact]
    public void Identical_configurations_loosen_nothing() => Assert.Empty(Loosened(RuleConfig.Defaults(), RuleConfig.Defaults()));

    [Fact]
    public void Lowering_block_to_flag_loosens() =>
        Assert.Equal([RuleCodes.DuplicateExact], Loosened(RuleConfig.Defaults(), With(RuleCodes.DuplicateExact, true, RuleSeverity.Flag)));

    [Fact]
    public void Raising_flag_to_block_tightens() =>
        Assert.Empty(Loosened(RuleConfig.Defaults(), With(RuleCodes.DuplicateFuzzy, true, RuleSeverity.Block)));

    [Theory]
    [InlineData(RuleCodes.DuplicateExact)]
    [InlineData(RuleCodes.DuplicateSameNumber)]
    [InlineData(RuleCodes.PoMatch)]
    [InlineData(RuleCodes.AmountTotals)]
    [InlineData(RuleCodes.TaxVat)]
    [InlineData(RuleCodes.DateSanity)]
    public void Switching_an_enabled_rule_off_loosens(string rule) =>
        Assert.Equal([rule], Loosened(RuleConfig.Defaults(), With(rule, false, RuleCatalog.Find(rule)!.DefaultSeverity)));

    [Fact]
    public void Switching_a_rule_on_tightens() =>
        Assert.Empty(Loosened(RuleConfig.Defaults(), With(RuleCodes.PoRequired, true, RuleSeverity.Flag)));

    [Fact]
    public void A_rule_that_was_off_cannot_be_loosened_further()
    {
        var off = With(RuleCodes.PoRequired, false, RuleSeverity.Block);
        Assert.Empty(Loosened(off, With(RuleCodes.PoRequired, false, RuleSeverity.Flag)));
        Assert.Empty(Loosened(off, With(RuleCodes.PoRequired, true, RuleSeverity.Flag))); // switching on, even weakly, adds a check
    }

    [Theory]
    // thresholds where a larger value accepts more
    [InlineData(RuleCodes.PoMatch, "priceTolerancePercent", 3, true)]
    [InlineData(RuleCodes.PoMatch, "priceTolerancePercent", 1, false)]
    [InlineData(RuleCodes.PoMatch, "overBillingPercent", 5, true)]
    [InlineData(RuleCodes.AmountTotals, "toleranceMinorUnits", 2, true)]
    [InlineData(RuleCodes.AmountTotals, "toleranceMinorUnits", 0, false)]
    [InlineData(RuleCodes.DateSanity, "maxAgeDays", 400, true)]
    [InlineData(RuleCodes.DateSanity, "maxAgeDays", 300, false)]
    // thresholds where a smaller value catches less
    [InlineData(RuleCodes.DuplicateFuzzy, "amountTolerancePercent", 0.5, true)]
    [InlineData(RuleCodes.DuplicateFuzzy, "amountTolerancePercent", 3, false)]
    [InlineData(RuleCodes.DuplicateFuzzy, "dateWindowDays", 3, true)]
    [InlineData(RuleCodes.DuplicateFuzzy, "dateWindowDays", 30, false)]
    // similarity: requiring a closer match catches less
    [InlineData(RuleCodes.DuplicateFuzzy, "numberSimilarity", 0.95, true)]
    [InlineData(RuleCodes.DuplicateFuzzy, "numberSimilarity", 0.7, false)]
    public void Thresholds_loosen_in_the_direction_the_catalogue_names(string rule, string parameter, double value, bool expectLoosened)
    {
        var def = RuleCatalog.Find(rule)!;
        var changed = With(rule, true, def.DefaultSeverity, (parameter, (decimal)value));
        Assert.Equal(expectLoosened, Loosened(RuleConfig.Defaults(), changed).Contains(rule));
    }

    [Fact]
    public void Several_rules_are_reported_together()
    {
        var from = RuleConfig.Defaults();
        var rules = new Dictionary<string, RuleSetting>(from.Rules)
        {
            [RuleCodes.DuplicateExact] = new(true, RuleSeverity.Flag, new Dictionary<string, decimal>()),
            [RuleCodes.TaxVat] = new(false, RuleSeverity.Flag, new Dictionary<string, decimal> { ["toleranceMinorUnits"] = 1m }),
            [RuleCodes.PoRequired] = new(true, RuleSeverity.Block, new Dictionary<string, decimal>()),
        };
        Assert.Equal([RuleCodes.DuplicateExact, RuleCodes.TaxVat], Loosened(from, RuleConfig.Normalize(rules)));
    }

    [Fact]
    public void Tightening_one_rule_while_loosening_another_still_counts_as_loosening()
    {
        var rules = new Dictionary<string, RuleSetting>(RuleConfig.Defaults().Rules)
        {
            [RuleCodes.DuplicateFuzzy] = new(true, RuleSeverity.Block, new Dictionary<string, decimal> { ["amountTolerancePercent"] = 1m, ["dateWindowDays"] = 7m, ["numberSimilarity"] = 0.85m }),
            [RuleCodes.AmountTotals] = new(true, RuleSeverity.Flag, new Dictionary<string, decimal> { ["toleranceMinorUnits"] = 1m }),
        };
        Assert.Equal([RuleCodes.AmountTotals], Loosened(RuleConfig.Defaults(), RuleConfig.Normalize(rules)));
    }
}

public class VerificationRuleSetTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 8, 0, 0, TimeSpan.Zero);
    private static readonly Guid Maker = Guid.NewGuid();

    private static VerificationRuleSet Pending(string loosened = "duplicate.exact") =>
        new() { Id = Guid.NewGuid(), CreatedBy = Maker, Status = RuleSetStatus.Pending, LoosenedRules = loosened };

    [Fact]
    public void A_different_person_approves()
    {
        var set = Pending();
        var checker = Guid.NewGuid();
        set.Approve(checker, " fine ", Now);
        Assert.Equal(RuleSetStatus.Active, set.Status);
        Assert.Equal(checker, set.DecidedBy);
        Assert.Equal("fine", set.DecisionComment);
    }

    [Fact]
    public void The_maker_cannot_approve_or_reject()
    {
        var set = Pending();
        Assert.Throws<SegregationOfDutiesException>(() => set.Approve(Maker, null, Now));
        Assert.Throws<SegregationOfDutiesException>(() => set.Reject(Maker, "no", Now));
        Assert.Equal(RuleSetStatus.Pending, set.Status);
    }

    [Fact]
    public void Rejecting_needs_a_reason() =>
        Assert.Equal("reason_required", Assert.Throws<ValidationFailedException>(() => Pending().Reject(Guid.NewGuid(), " ", Now)).Code);

    [Fact]
    public void Only_the_maker_can_cancel()
    {
        var set = Pending();
        Assert.Equal("not_the_maker", Assert.Throws<ConflictException>(() => set.Cancel(Guid.NewGuid(), Now)).Code);
        set.Cancel(Maker, Now);
        Assert.Equal(RuleSetStatus.Cancelled, set.Status);
    }

    [Theory]
    [InlineData(RuleSetStatus.Active)]
    [InlineData(RuleSetStatus.Superseded)]
    [InlineData(RuleSetStatus.Rejected)]
    [InlineData(RuleSetStatus.Cancelled)]
    public void Decided_versions_cannot_be_decided_again(string status)
    {
        var set = Pending();
        set.Status = status;
        Assert.Equal("change_not_pending", Assert.Throws<ConflictException>(() => set.Approve(Guid.NewGuid(), null, Now)).Code);
        Assert.Throws<ConflictException>(() => set.Reject(Guid.NewGuid(), "x", Now));
        Assert.Throws<ConflictException>(() => set.Cancel(Maker, Now));
    }

    [Fact]
    public void A_loosening_version_cannot_be_activated_without_approval() =>
        Assert.Equal("approval_required", Assert.Throws<ConflictException>(() => Pending().ActivateImmediately(Now)).Code);

    [Fact]
    public void A_tightening_version_activates_without_approval()
    {
        var set = Pending("");
        set.ActivateImmediately(Now);
        Assert.Equal(RuleSetStatus.Active, set.Status);
    }
}
