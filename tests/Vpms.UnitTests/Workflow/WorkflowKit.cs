using Vpms.Workflow.Domain;

namespace Vpms.UnitTests.Workflow;

/// <summary>Builders for workflow definitions and people used by the engine tests.</summary>
internal static class WorkflowKit
{
    public static readonly Guid Maker = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    public static readonly Guid A = Guid.Parse("00000000-0000-0000-0000-0000000000b1");
    public static readonly Guid B = Guid.Parse("00000000-0000-0000-0000-0000000000b2");
    public static readonly Guid C = Guid.Parse("00000000-0000-0000-0000-0000000000b3");
    public static readonly Guid D = Guid.Parse("00000000-0000-0000-0000-0000000000b4");
    public static readonly Guid E = Guid.Parse("00000000-0000-0000-0000-0000000000b5");

    public static ApproverSpec User(Guid id) => new(ApproverKind.User, id.ToString());
    public static ApproverSpec Role(string name) => new(ApproverKind.Role, name);

    public static StepSpec Approval(string id, string? next, string mode = ApprovalMode.AnyOne, int quorum = 0, params ApproverSpec[] approvers) =>
        new() { Id = id, Type = StepType.Approval, Next = next, Mode = mode, Quorum = quorum, Approvers = approvers };

    public static StepSpec Single(string id, string? next, Guid user) => Approval(id, next, ApprovalMode.AnyOne, 0, User(user));

    public static StepSpec Parallel(string id, string? next, string join, params string[] branches) =>
        new() { Id = id, Type = StepType.Parallel, Next = next, Join = join, Branches = branches };

    public static StepSpec Condition(string id, string? @default, params CaseSpec[] cases) =>
        new() { Id = id, Type = StepType.Condition, Default = @default, Cases = cases };

    public static CaseSpec When(string? next, params ClauseSpec[] clauses) => new(clauses, next);
    public static ClauseSpec Clause(string field, string op, params string[] values) => new(field, op, values);

    public static WorkflowDefinitionSpec Def(string start, params StepSpec[] steps) => new() { Name = "Test", Start = start, Steps = steps };

    public static ApproverResolver Resolver(Dictionary<string, Guid[]>? roles = null) => a =>
        a.Kind == ApproverKind.User ? [Guid.Parse(a.Value)] : (roles is not null && roles.TryGetValue(a.Value, out var us) ? us : []);

    public static SubjectFacts Facts(decimal amount = 100) => SubjectFacts.Of(amount);
}
