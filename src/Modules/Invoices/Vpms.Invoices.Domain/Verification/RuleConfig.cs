using Vpms.BuildingBlocks.Errors;

namespace Vpms.Invoices.Domain.Verification;

public sealed record RuleSetting(bool Enabled, string Severity, IReadOnlyDictionary<string, decimal> Parameters);

/// <summary>The rule settings of one legal entity: every rule of the catalogue, with its switch, severity and thresholds.</summary>
public sealed record RuleConfig(IReadOnlyDictionary<string, RuleSetting> Rules)
{
    public static RuleConfig Defaults() => new(RuleCatalog.All.ToDictionary(
        r => r.Code,
        r => new RuleSetting(r.DefaultEnabled, r.DefaultSeverity, r.Parameters.ToDictionary(p => p.Name, p => p.Default)),
        StringComparer.Ordinal));

    public RuleSetting Get(string code) => Rules[code];

    public decimal Param(string code, string name) => Rules[code].Parameters[name];

    /// <summary>
    /// Validates a submitted configuration and fills in anything it leaves out from the defaults (so rules and parameters added in
    /// later releases appear with safe defaults). Unknown rules or parameters, impossible severities and out-of-range values are refused.
    /// </summary>
    public static RuleConfig Normalize(IReadOnlyDictionary<string, RuleSetting>? submitted)
    {
        submitted ??= new Dictionary<string, RuleSetting>();
        foreach (var code in submitted.Keys)
        {
            if (RuleCatalog.Find(code) is null) throw new ValidationFailedException("unknown_rule", $"Unknown verification rule '{code}'.");
        }

        var result = new Dictionary<string, RuleSetting>(StringComparer.Ordinal);
        foreach (var def in RuleCatalog.All)
        {
            if (!submitted.TryGetValue(def.Code, out var setting))
            {
                result[def.Code] = new RuleSetting(def.DefaultEnabled, def.DefaultSeverity, def.Parameters.ToDictionary(p => p.Name, p => p.Default));
                continue;
            }

            if (!RuleSeverity.All.Contains(setting.Severity)) throw new ValidationFailedException("invalid_severity", $"Severity of '{def.Code}' must be Block or Flag.");
            if (setting.Severity == RuleSeverity.Flag && !def.CanFlag) throw new ValidationFailedException("invalid_severity", $"'{def.Code}' must always block.");
            if (!setting.Enabled && !def.CanDisable) throw new ValidationFailedException("rule_not_disableable", $"'{def.Code}' cannot be switched off.");

            var parameters = new Dictionary<string, decimal>(StringComparer.Ordinal);
            foreach (var name in (setting.Parameters ?? new Dictionary<string, decimal>()).Keys)
            {
                if (def.Parameter(name) is null) throw new ValidationFailedException("unknown_parameter", $"'{def.Code}' has no parameter '{name}'.");
            }

            foreach (var p in def.Parameters)
            {
                var value = setting.Parameters is not null && setting.Parameters.TryGetValue(p.Name, out var v) ? v : p.Default;
                if (value < p.Min || value > p.Max)
                {
                    throw new ValidationFailedException("parameter_out_of_range", $"'{def.Code}.{p.Name}' must be between {p.Min} and {p.Max}.");
                }

                parameters[p.Name] = value;
            }

            result[def.Code] = new RuleSetting(setting.Enabled, setting.Severity, parameters);
        }

        return new RuleConfig(result);
    }

    /// <summary>
    /// The rules a change makes looser, that is, rules that would catch less afterwards: switched off, Block lowered to Flag, or a
    /// threshold moved in the loosening direction. Tightening changes are not listed.
    /// </summary>
    public static IReadOnlyList<string> LoosenedRules(RuleConfig from, RuleConfig to)
    {
        var loosened = new List<string>();
        foreach (var def in RuleCatalog.All)
        {
            var before = from.Rules[def.Code];
            var after = to.Rules[def.Code];
            if (!before.Enabled) continue; // a rule that was off cannot get looser; switching it on only tightens

            var looser = !after.Enabled || RuleSeverity.Strength(after.Severity) < RuleSeverity.Strength(before.Severity);
            if (!looser)
            {
                looser = def.Parameters.Any(p =>
                {
                    var b = before.Parameters[p.Name];
                    var a = after.Parameters[p.Name];
                    return p.LoosenedBy == Loosening.WhenIncreased ? a > b : a < b;
                });
            }

            if (looser) loosened.Add(def.Code);
        }

        return loosened;
    }
}
