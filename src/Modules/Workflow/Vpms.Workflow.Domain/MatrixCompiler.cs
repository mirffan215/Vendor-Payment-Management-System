using System.Globalization;
using System.Text.Json;
using Vpms.Workflow.Domain.Errors;

namespace Vpms.Workflow.Domain;

/// <summary>An approval matrix rule as stored by customer administration.</summary>
public sealed record MatrixRuleInput(string Name, int Priority, string ConditionsJson, string ApproverResolutionJson, int Levels);

/// <summary>
/// Turns an entity's approval matrix into a workflow definition: a condition step tries the rules in priority order, and each
/// rule becomes a chain of approval levels. An invoice that matches no rule has no route, which is an error, never an approval.
///
/// Conditions: {"amountFrom":0,"amountTo":10000,"currency":"SAR","vendors":["guid"],"dimensions":{"CostCenter":["CC1"]}} - all optional,
/// amounts inclusive. Resolution: {"levels":[{"approvers":[{"kind":"Role","value":"Finance Manager"}],"mode":"AnyOne","dueHours":48,
/// "reminderHours":24,"escalateTo":{"kind":"Role","value":"CFO"}}]} with one entry per level.
/// </summary>
public static class MatrixCompiler
{
    public const string RouteStep = "route";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static WorkflowDefinitionSpec Compile(string name, IReadOnlyList<MatrixRuleInput> rules)
    {
        if (rules.Count == 0) throw new WorkflowDefinitionException("matrix_empty", "The legal entity has no active approval matrix rules.");

        var steps = new List<StepSpec>();
        var cases = new List<CaseSpec>();
        for (var i = 0; i < rules.Count; i++)
        {
            var rule = rules[i];
            var conditions = ParseConditions(rule);
            var resolution = Parse<Resolution>(rule.ApproverResolutionJson, rule.Name, "approver resolution");
            var levels = resolution.Levels ?? [];
            if (levels.Count == 0 || levels.Count != rule.Levels)
            {
                throw new WorkflowDefinitionException("matrix_levels", $"Rule '{rule.Name}' declares {rule.Levels} level(s) but defines {levels.Count}.");
            }

            for (var l = 0; l < levels.Count; l++)
            {
                var level = levels[l];
                steps.Add(new StepSpec
                {
                    Id = $"m{i + 1}-l{l + 1}", Type = StepType.Approval,
                    Next = l + 1 < levels.Count ? $"m{i + 1}-l{l + 2}" : null,
                    Approvers = (level.Approvers ?? []).Select(a => new ApproverSpec(a.Kind ?? "", a.Value ?? "")).ToList(),
                    Mode = level.Mode ?? ApprovalMode.AnyOne, Quorum = level.Quorum ?? 0,
                    DueHours = level.DueHours, ReminderHours = level.ReminderHours,
                    EscalateTo = level.EscalateTo is null ? null : new ApproverSpec(level.EscalateTo.Kind ?? "", level.EscalateTo.Value ?? ""),
                });
            }

            cases.Add(new CaseSpec(Clauses(conditions), $"m{i + 1}-l1"));
        }

        steps.Insert(0, new StepSpec { Id = RouteStep, Type = StepType.Condition, Cases = cases, Default = null });
        var spec = new WorkflowDefinitionSpec { Name = name, Start = RouteStep, Steps = steps };
        var errors = DefinitionValidator.Validate(spec);
        if (errors.Count > 0) throw new WorkflowDefinitionException("matrix_invalid", errors[0].StepId is null ? errors[0].Message : $"{errors[0].Message} ({errors[0].StepId})");
        return spec;
    }

    private static IReadOnlyList<ClauseSpec> Clauses(Conditions c)
    {
        var clauses = new List<ClauseSpec>();
        if (c.AmountFrom is { } from) clauses.Add(new ClauseSpec("amount", ConditionOp.Ge, [from.ToString(CultureInfo.InvariantCulture)]));
        if (c.AmountTo is { } to) clauses.Add(new ClauseSpec("amount", ConditionOp.Le, [to.ToString(CultureInfo.InvariantCulture)]));
        if (!string.IsNullOrWhiteSpace(c.Currency)) clauses.Add(new ClauseSpec("currency", ConditionOp.Eq, [c.Currency.Trim().ToUpperInvariant()]));
        if (c.Vendors is { Count: > 0 }) clauses.Add(new ClauseSpec("vendor", ConditionOp.In, c.Vendors));
        foreach (var (dimension, values) in c.Dimensions ?? [])
        {
            if (values.Count > 0) clauses.Add(new ClauseSpec($"dimension:{dimension}", ConditionOp.In, values));
        }

        // A rule with no conditions applies to every invoice.
        if (clauses.Count == 0) clauses.Add(new ClauseSpec("amount", ConditionOp.Ge, ["0"]));
        return clauses;
    }

    private static Conditions ParseConditions(MatrixRuleInput rule) => Parse<Conditions>(rule.ConditionsJson, rule.Name, "conditions");

    private static T Parse<T>(string json, string rule, string what)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, Json) ?? throw new JsonException();
        }
        catch (JsonException)
        {
            throw new WorkflowDefinitionException("matrix_json", $"Rule '{rule}': the {what} are not in the expected format.");
        }
    }

    private sealed record Conditions(decimal? AmountFrom, decimal? AmountTo, string? Currency, List<string>? Vendors, Dictionary<string, List<string>>? Dimensions);

    private sealed record Resolution(List<Level>? Levels);

    private sealed record Level(List<Approver>? Approvers, string? Mode, int? Quorum, int? DueHours, int? ReminderHours, Approver? EscalateTo);

    private sealed record Approver(string? Kind, string? Value);
}
