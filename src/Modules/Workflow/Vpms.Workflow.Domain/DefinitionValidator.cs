using System.Globalization;
using System.Text.RegularExpressions;

namespace Vpms.Workflow.Domain;

public sealed record DefinitionError(string Code, string Message, string? StepId = null);

/// <summary>Checks a definition before it can be stored, so a stored definition can always be run.</summary>
public static partial class DefinitionValidator
{
    public const int MaxSteps = 200;

    [GeneratedRegex("^[A-Za-z0-9_-]{1,40}$")]
    private static partial Regex IdPattern();

    public static IReadOnlyList<DefinitionError> Validate(WorkflowDefinitionSpec spec)
    {
        var errors = new List<DefinitionError>();
        void Add(string code, string message, string? step = null) => errors.Add(new DefinitionError(code, message, step));

        if (string.IsNullOrWhiteSpace(spec.Name) || spec.Name.Length > 100) Add("name_invalid", "A workflow needs a name of up to 100 characters.");
        if (spec.Steps.Count is 0 or > MaxSteps) Add("steps_count", $"A workflow needs between 1 and {MaxSteps} steps.");

        var ids = new HashSet<string>();
        foreach (var s in spec.Steps)
        {
            if (!IdPattern().IsMatch(s.Id)) Add("step_id_invalid", "Step ids use letters, digits, '-' and '_' (up to 40).", s.Id);
            else if (!ids.Add(s.Id)) Add("step_id_duplicate", "Step ids must be unique.", s.Id);
        }

        if (!ids.Contains(spec.Start)) Add("start_missing", "The start step does not exist.");

        foreach (var s in spec.Steps)
        {
            if (!StepType.All.Contains(s.Type))
            {
                Add("step_type_invalid", "Unknown step type.", s.Id);
                continue;
            }

            foreach (var target in s.Successors().Where(t => !ids.Contains(t)))
            {
                Add("step_target_missing", $"Step '{target}' does not exist.", s.Id);
            }

            switch (s.Type)
            {
                case StepType.Approval: ValidateApproval(s, Add); break;
                case StepType.Parallel: ValidateParallel(s, Add); break;
                default: ValidateCondition(s, Add); break;
            }
        }

        if (errors.Count == 0)
        {
            ValidateGraph(spec, Add);
        }

        return errors;
    }

    private static void ValidateApproval(StepSpec s, Action<string, string, string?> add)
    {
        if (s.Approvers.Count is 0 or > 50) add("approvers_invalid", "An approval step needs 1 to 50 approvers.", s.Id);
        foreach (var a in s.Approvers)
        {
            if (!ApproverKind.Values.Contains(a.Kind) || string.IsNullOrWhiteSpace(a.Value) ||
                (a.Kind == ApproverKind.User && !Guid.TryParse(a.Value, out _)))
            {
                add("approver_invalid", "Each approver is a user id or a role name.", s.Id);
            }
        }

        if (!ApprovalMode.Values.Contains(s.Mode)) add("mode_invalid", "Unknown approval mode.", s.Id);
        if (s.Mode == ApprovalMode.Quorum && s.Quorum is < 1 or > 50) add("quorum_invalid", "A quorum is between 1 and 50.", s.Id);
        if (s.Mode == ApprovalMode.Quorum && s.Approvers.All(a => a.Kind == ApproverKind.User) && s.Quorum > s.Approvers.Count)
        {
            add("quorum_invalid", "The quorum is larger than the number of approvers.", s.Id);
        }

        if (s.DueHours is <= 0) add("due_invalid", "The deadline must be a positive number of hours.", s.Id);
        if (s.ReminderHours is <= 0 || (s.ReminderHours is not null && (s.DueHours is null || s.ReminderHours >= s.DueHours)))
        {
            add("reminder_invalid", "A reminder needs a deadline and must come before it.", s.Id);
        }

        if (s.EscalateTo is not null && (s.DueHours is null || !ApproverKind.Values.Contains(s.EscalateTo.Kind) || string.IsNullOrWhiteSpace(s.EscalateTo.Value) ||
            (s.EscalateTo.Kind == ApproverKind.User && !Guid.TryParse(s.EscalateTo.Value, out _))))
        {
            add("escalation_invalid", "Escalation needs a deadline and a valid escalation approver.", s.Id);
        }
    }

    private static void ValidateParallel(StepSpec s, Action<string, string, string?> add)
    {
        if (s.Branches.Count < 2) add("branches_invalid", "A parallel step needs at least two branches.", s.Id);
        if (s.Branches.Distinct().Count() != s.Branches.Count) add("branches_invalid", "Branches must start at different steps.", s.Id);
        if (!JoinMode.Values.Contains(s.Join)) add("join_invalid", "Unknown join mode.", s.Id);
    }

    private static void ValidateCondition(StepSpec s, Action<string, string, string?> add)
    {
        if (s.Cases.Count == 0) add("cases_invalid", "A condition step needs at least one case.", s.Id);
        foreach (var c in s.Cases)
        {
            if (c.All.Count == 0) add("case_empty", "A case needs at least one clause.", s.Id);
            foreach (var clause in c.All) ValidateClause(clause, s.Id, add);
        }
    }

    private static void ValidateClause(ClauseSpec c, string step, Action<string, string, string?> add)
    {
        if (!Conditions.IsKnownField(c.Field)) { add("clause_field", $"Unknown field '{c.Field}'.", step); return; }
        if (!ConditionOp.Values.Contains(c.Op)) { add("clause_op", $"Unknown operator '{c.Op}'.", step); return; }
        if (c.Values.Count == 0 || (c.Op != ConditionOp.In && c.Values.Count != 1)) { add("clause_value", "The clause has the wrong number of values.", step); return; }

        if (c.Field == "amount")
        {
            if (c.Values.Any(v => !decimal.TryParse(v, Conditions.AmountStyle, CultureInfo.InvariantCulture, out _))) add("clause_value", "Amounts must be numbers.", step);
            return;
        }

        if (ConditionOp.Ordering.Contains(c.Op)) add("clause_op", "Only amounts can be compared with < or >.", step);
        if (c.Field == "vendor" && c.Values.Any(v => !Guid.TryParse(v, out _))) add("clause_value", "Vendor values are vendor ids.", step);
    }

    private static void ValidateGraph(WorkflowDefinitionSpec spec, Action<string, string, string?> add)
    {
        // Cycles: every step must be left before it is re-entered.
        var state = new Dictionary<string, int>();
        bool cyclic = false;
        void Visit(string id)
        {
            if (cyclic) return;
            state[id] = 1;
            foreach (var next in spec.Find(id)!.Successors())
            {
                if (!state.TryGetValue(next, out var st)) Visit(next);
                else if (st == 1) { cyclic = true; return; }
            }

            state[id] = 2;
        }

        Visit(spec.Start);
        if (cyclic)
        {
            add("cycle", "A workflow must not loop back to an earlier step.", null);
            return;
        }

        foreach (var s in spec.Steps.Where(s => !state.ContainsKey(s.Id)))
        {
            add("step_unreachable", "This step can never be reached.", s.Id);
        }

        foreach (var p in spec.Steps.Where(s => s.Type == StepType.Parallel))
        {
            var seen = new HashSet<string>();
            var after = p.Next is null ? [] : Reach(spec, p.Next);
            foreach (var b in p.Branches)
            {
                var reach = Reach(spec, b);
                if (reach.Overlaps(seen) || reach.Overlaps(after))
                {
                    add("branches_overlap", "Parallel branches must not share steps with each other or with the steps after the join.", p.Id);
                }

                seen.UnionWith(reach);
            }
        }
    }

    private static HashSet<string> Reach(WorkflowDefinitionSpec spec, string from)
    {
        var seen = new HashSet<string>();
        var stack = new Stack<string>([from]);
        while (stack.Count > 0)
        {
            var id = stack.Pop();
            if (!seen.Add(id)) continue;
            foreach (var n in spec.Find(id)!.Successors()) stack.Push(n);
        }

        return seen;
    }
}
