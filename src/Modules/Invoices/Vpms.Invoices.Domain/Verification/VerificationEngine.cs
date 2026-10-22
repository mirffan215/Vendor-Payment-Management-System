using Vpms.Invoices.Domain.Verification.Rules;

namespace Vpms.Invoices.Domain.Verification;

/// <summary>Runs the enabled rules of an entity's configuration over one invoice. Pure: the same input always gives the same result.</summary>
public sealed class VerificationEngine(IEnumerable<IVerificationRule> rules)
{
    private readonly Dictionary<string, IVerificationRule> _rules = rules.ToDictionary(r => r.Code);

    public static VerificationEngine Standard() => new(
    [
        new DuplicateExactRule(), new DuplicateSameNumberRule(), new DuplicateFuzzyRule(), new PoRequiredRule(), new PoMatchRule(),
        new AmountTotalsRule(), new AmountLineMathRule(), new TaxVatRule(), new DateSanityRule(), new VendorStateRule(),
    ]);

    /// <summary>One result per catalogue rule, in catalogue order. Disabled rules are reported as Skipped so the record shows what ran.</summary>
    public IReadOnlyList<RuleResult> Run(VerificationInput input, RuleConfig config)
    {
        var results = new List<RuleResult>();
        foreach (var def in RuleCatalog.All)
        {
            var setting = config.Get(def.Code);
            if (!setting.Enabled || !_rules.TryGetValue(def.Code, out var rule))
            {
                results.Add(new RuleResult(def.Code, Outcome.Skipped, setting.Severity, []));
                continue;
            }

            var findings = rule.Evaluate(input, setting);
            results.Add(new RuleResult(def.Code, findings.Count == 0 ? Outcome.Passed : Outcome.Failed, setting.Severity, findings));
        }

        return results;
    }
}
