using Vpms.Workflow.Domain;
using Vpms.Workflow.Domain.Errors;
using static Vpms.UnitTests.Workflow.WorkflowKit;

namespace Vpms.UnitTests.Workflow;

public class MatrixCompilerTests
{
    private static string Level(string role = "Approver", string extra = "") =>
        $"{{\"approvers\":[{{\"kind\":\"Role\",\"value\":\"{role}\"}}]{extra}}}";

    private static string Levels(params string[] l) => $"{{\"levels\":[{string.Join(',', l)}]}}";

    private static MatrixRuleInput Rule(string name, int priority, string conditions, int levels, string? resolution = null) =>
        new(name, priority, conditions, resolution ?? Levels(Enumerable.Range(0, levels).Select(_ => Level()).ToArray()), levels);

    private static readonly ApproverResolver Anyone = a => a.Kind == ApproverKind.Role ? [A, B, C] : [Guid.Parse(a.Value)];

    private static string Route(WorkflowDefinitionSpec def, decimal amount, string currency = "SAR", Guid? vendor = null, Dictionary<string, string>? dims = null) =>
        WorkflowSimulator.Run(def, SubjectFacts.Of(amount, currency, vendor, "Manual", dims), [Maker], Anyone, []).Run.PendingTasks.Single().StepId;

    [Fact]
    public void Rules_are_tried_in_the_order_given_and_the_first_match_wins()
    {
        var def = MatrixCompiler.Compile("m", [Rule("Any", 1, "{}", 1), Rule("Big", 2, "{\"amountFrom\":1000}", 1)]);
        Assert.Equal("m1-l1", Route(def, 5000));
    }

    [Theory]
    [InlineData(0, "m1-l1")]
    [InlineData(10000, "m1-l1")]
    [InlineData(10000.01, "m2-l1")]
    [InlineData(250000, "m2-l1")]
    public void Amount_bounds_are_inclusive(double amount, string step)
    {
        var def = MatrixCompiler.Compile("m", [Rule("Small", 1, "{\"amountTo\":10000}", 1), Rule("Large", 2, "{\"amountFrom\":10000.01}", 2)]);
        Assert.Equal(step, Route(def, (decimal)amount));
    }

    [Fact]
    public void A_rule_with_levels_becomes_a_chain_of_approval_steps()
    {
        var def = MatrixCompiler.Compile("m", [Rule("Two", 1, "{}", 2)]);
        Assert.Equal(["route", "m1-l1", "m1-l2"], def.Steps.Select(s => s.Id));
        Assert.Equal("m1-l2", def.Find("m1-l1")!.Next);
        Assert.Null(def.Find("m1-l2")!.Next);
    }

    [Fact]
    public void Levels_carry_mode_deadlines_and_escalation()
    {
        var level = Level("CFO", ",\"mode\":\"Quorum\",\"quorum\":2,\"dueHours\":48,\"reminderHours\":24,\"escalateTo\":{\"kind\":\"Role\",\"value\":\"Board\"}");
        var def = MatrixCompiler.Compile("m", [new MatrixRuleInput("R", 1, "{}", Levels(level), 1)]);
        var step = def.Find("m1-l1")!;
        Assert.Equal(ApprovalMode.Quorum, step.Mode);
        Assert.Equal(2, step.Quorum);
        Assert.Equal(48, step.DueHours);
        Assert.Equal(24, step.ReminderHours);
        Assert.Equal("Board", step.EscalateTo!.Value);
    }

    [Fact]
    public void Vendor_currency_and_dimension_conditions_are_combined_with_and()
    {
        var vendor = Guid.NewGuid();
        var def = MatrixCompiler.Compile("m", [Rule("Special", 1, $"{{\"vendors\":[\"{vendor}\"],\"currency\":\"usd\",\"dimensions\":{{\"Project\":[\"P1\"]}}}}", 1)]);
        var p1 = new Dictionary<string, string> { ["Project"] = "P1" };

        Assert.Equal("m1-l1", Route(def, 1, "USD", vendor, p1));
        Assert.Throws<Vpms.BuildingBlocks.Errors.ConflictException>(() => Route(def, 1, "SAR", vendor, p1));
        Assert.Throws<Vpms.BuildingBlocks.Errors.ConflictException>(() => Route(def, 1, "USD", Guid.NewGuid(), p1));
        Assert.Throws<Vpms.BuildingBlocks.Errors.ConflictException>(() => Route(def, 1, "USD", vendor, new Dictionary<string, string> { ["Project"] = "P2" }));
    }

    [Fact]
    public void An_invoice_that_matches_no_rule_has_no_route()
    {
        var def = MatrixCompiler.Compile("m", [Rule("Small", 1, "{\"amountTo\":100}", 1)]);
        var ex = Assert.Throws<Vpms.BuildingBlocks.Errors.ConflictException>(() => Route(def, 101));
        Assert.Equal("no_route", ex.Code);
    }

    [Theory]
    [InlineData("not json", "matrix_json")]
    [InlineData("[]", "matrix_json")]
    public void Malformed_conditions_are_refused(string conditions, string code) =>
        Assert.Equal(code, Assert.Throws<WorkflowDefinitionException>(() => MatrixCompiler.Compile("m", [Rule("R", 1, conditions, 1)])).Code);

    [Fact]
    public void The_declared_level_count_must_match_the_definition()
    {
        var ex = Assert.Throws<WorkflowDefinitionException>(() => MatrixCompiler.Compile("m", [new MatrixRuleInput("R", 1, "{}", Levels(Level()), 2)]));
        Assert.Equal("matrix_levels", ex.Code);
    }

    [Fact]
    public void An_empty_matrix_or_bad_approvers_are_refused()
    {
        Assert.Equal("matrix_empty", Assert.Throws<WorkflowDefinitionException>(() => MatrixCompiler.Compile("m", [])).Code);
        var noApprovers = new MatrixRuleInput("R", 1, "{}", "{\"levels\":[{\"approvers\":[]}]}", 1);
        Assert.Equal("matrix_invalid", Assert.Throws<WorkflowDefinitionException>(() => MatrixCompiler.Compile("m", [noApprovers])).Code);
    }
}
