using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vpms.Workflow.Domain;

public static class StepType
{
    public const string Approval = "Approval";
    public const string Parallel = "Parallel";
    public const string Condition = "Condition";

    public static readonly IReadOnlyList<string> All = [Approval, Parallel, Condition];
}

/// <summary>How many of a step's approvers must approve before the step is complete.</summary>
public static class ApprovalMode
{
    public const string AnyOne = "AnyOne";
    public const string All = "All";
    public const string Quorum = "Quorum";

    public static readonly IReadOnlyList<string> Values = [AnyOne, All, Quorum];
}

/// <summary>Whether a parallel step continues when every branch is done or when the first one is.</summary>
public static class JoinMode
{
    public const string All = "All";
    public const string Any = "Any";

    public static readonly IReadOnlyList<string> Values = [All, Any];
}

public static class ApproverKind
{
    public const string User = "User";
    public const string Role = "Role";

    public static readonly IReadOnlyList<string> Values = [User, Role];
}

public static class ConditionOp
{
    public const string Eq = "eq";
    public const string Ne = "ne";
    public const string Gt = "gt";
    public const string Ge = "ge";
    public const string Lt = "lt";
    public const string Le = "le";
    public const string In = "in";

    public static readonly IReadOnlyList<string> Values = [Eq, Ne, Gt, Ge, Lt, Le, In];
    public static readonly IReadOnlyList<string> Ordering = [Gt, Ge, Lt, Le];
}

/// <summary>Who may decide a step: one named user, or everyone holding a role for the legal entity.</summary>
public sealed record ApproverSpec(string Kind, string Value);

/// <summary>One comparison of a fact against fixed values. The closed set of fields and operators is the whole expression language.</summary>
public sealed record ClauseSpec(string Field, string Op, IReadOnlyList<string> Values);

/// <summary>A branch of a Condition step: taken when every clause holds.</summary>
public sealed record CaseSpec(IReadOnlyList<ClauseSpec> All, string? Next);

public sealed class StepSpec
{
    public string Id { get; init; } = "";
    public string Type { get; init; } = StepType.Approval;

    /// <summary>The step after this one; null ends the path (the whole workflow, or the branch of a parallel step).</summary>
    public string? Next { get; init; }

    // Approval
    public IReadOnlyList<ApproverSpec> Approvers { get; init; } = [];
    public string Mode { get; init; } = ApprovalMode.AnyOne;
    public int Quorum { get; init; }
    public int? DueHours { get; init; }
    public int? ReminderHours { get; init; }
    public ApproverSpec? EscalateTo { get; init; }

    // Parallel
    public IReadOnlyList<string> Branches { get; init; } = [];
    public string Join { get; init; } = JoinMode.All;

    // Condition
    public IReadOnlyList<CaseSpec> Cases { get; init; } = [];
    public string? Default { get; init; }

    public IEnumerable<string> Successors()
    {
        switch (Type)
        {
            case StepType.Parallel:
                foreach (var b in Branches) yield return b;
                break;
            case StepType.Condition:
                foreach (var c in Cases.Where(c => c.Next is not null)) yield return c.Next!;
                if (Default is not null) yield return Default;
                break;
        }

        if (Type != StepType.Condition && Next is not null) yield return Next;
    }
}

/// <summary>The data a workflow is made of. Immutable once published as a version.</summary>
public sealed class WorkflowDefinitionSpec
{
    public string Name { get; init; } = "";
    public string Start { get; init; } = "";
    public IReadOnlyList<StepSpec> Steps { get; init; } = [];

    public StepSpec? Find(string? id) => id is null ? null : Steps.FirstOrDefault(s => s.Id == id);
}

public static class DefinitionJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string ToJson(WorkflowDefinitionSpec spec) => JsonSerializer.Serialize(spec, Options);

    public static WorkflowDefinitionSpec Parse(string json) =>
        JsonSerializer.Deserialize<WorkflowDefinitionSpec>(json, Options)
        ?? throw new Errors.WorkflowDefinitionException("invalid_definition", "The workflow definition is empty.");
}
