using Vpms.Workflow.Domain;
using static Vpms.UnitTests.Workflow.WorkflowKit;

namespace Vpms.UnitTests.Workflow;

public class DefinitionValidatorTests
{
    private static IEnumerable<string> Codes(WorkflowDefinitionSpec d) => DefinitionValidator.Validate(d).Select(e => e.Code);

    [Fact]
    public void A_good_definition_has_no_errors()
    {
        var def = Def("p", Parallel("p", "c", JoinMode.All, "a", "b"), Single("a", null, A), Single("b", null, B),
            Condition("c", null, When("e", Clause("amount", ConditionOp.Gt, "10"))), Single("e", null, C));
        Assert.Empty(DefinitionValidator.Validate(def));
    }

    [Fact]
    public void Structure_errors_are_reported()
    {
        Assert.Contains("start_missing", Codes(Def("nope", Single("a", null, A))));
        Assert.Contains("step_id_duplicate", Codes(Def("a", Single("a", null, A), Single("a", null, B))));
        Assert.Contains("step_id_invalid", Codes(Def("a b", Single("a b", null, A))));
        Assert.Contains("step_target_missing", Codes(Def("a", Single("a", "zzz", A))));
        Assert.Contains("steps_count", Codes(Def("a")));
        Assert.Contains("name_invalid", Codes(new WorkflowDefinitionSpec { Name = "", Start = "a", Steps = [Single("a", null, A)] }));
    }

    [Fact]
    public void Cycles_and_unreachable_steps_are_rejected()
    {
        Assert.Contains("cycle", Codes(Def("a", Single("a", "b", A), Single("b", "a", B))));
        Assert.Contains("cycle", Codes(Def("a", Single("a", "a", A))));
        Assert.Contains("step_unreachable", Codes(Def("a", Single("a", null, A), Single("orphan", null, B))));
    }

    [Fact]
    public void Approval_steps_are_checked()
    {
        Assert.Contains("approvers_invalid", Codes(Def("a", Approval("a", null))));
        Assert.Contains("approver_invalid", Codes(Def("a", Approval("a", null, ApprovalMode.AnyOne, 0, new ApproverSpec(ApproverKind.User, "not-a-guid")))));
        Assert.Contains("approver_invalid", Codes(Def("a", Approval("a", null, ApprovalMode.AnyOne, 0, new ApproverSpec("Group", "x")))));
        Assert.Contains("mode_invalid", Codes(Def("a", Approval("a", null, "Majority", 0, User(A)))));
        Assert.Contains("quorum_invalid", Codes(Def("a", Approval("a", null, ApprovalMode.Quorum, 3, User(A), User(B)))));
        Assert.Contains("quorum_invalid", Codes(Def("a", Approval("a", null, ApprovalMode.Quorum, 0, User(A)))));
        Assert.Empty(Codes(Def("a", Approval("a", null, ApprovalMode.Quorum, 2, Role("x"), Role("y")))));
    }

    [Fact]
    public void Deadline_reminder_and_escalation_must_be_consistent()
    {
        StepSpec Step(int? due, int? reminder, ApproverSpec? esc) =>
            new() { Id = "a", Approvers = [User(A)], DueHours = due, ReminderHours = reminder, EscalateTo = esc };

        Assert.Empty(Codes(Def("a", Step(48, 24, User(B)))));
        Assert.Contains("due_invalid", Codes(Def("a", Step(0, null, null))));
        Assert.Contains("reminder_invalid", Codes(Def("a", Step(null, 5, null))));
        Assert.Contains("reminder_invalid", Codes(Def("a", Step(10, 10, null))));
        Assert.Contains("escalation_invalid", Codes(Def("a", Step(null, null, User(B)))));
        Assert.Contains("escalation_invalid", Codes(Def("a", Step(10, null, new ApproverSpec(ApproverKind.User, "x")))));
    }

    [Fact]
    public void Parallel_steps_are_checked()
    {
        Assert.Contains("branches_invalid", Codes(Def("p", Parallel("p", null, JoinMode.All, "a"), Single("a", null, A))));
        Assert.Contains("branches_invalid", Codes(Def("p", Parallel("p", null, JoinMode.All, "a", "a"), Single("a", null, A))));
        Assert.Contains("join_invalid", Codes(Def("p", Parallel("p", null, "Some", "a", "b"), Single("a", null, A), Single("b", null, B))));
    }

    [Fact]
    public void Branches_must_not_share_steps_with_each_other_or_the_continuation()
    {
        Assert.Contains("branches_overlap", Codes(Def("p", Parallel("p", null, JoinMode.All, "a", "b"),
            Single("a", "shared", A), Single("b", "shared", B), Single("shared", null, C))));
        Assert.Contains("branches_overlap", Codes(Def("p", Parallel("p", "after", JoinMode.All, "a", "b"),
            Single("a", "after", A), Single("b", null, B), Single("after", null, C))));
    }

    [Theory]
    [InlineData("amount", "gt", "abc", "clause_value")]
    [InlineData("amount", "eq", "1,2", "clause_value")]
    [InlineData("colour", "eq", "red", "clause_field")]
    [InlineData("currency", "gt", "SAR", "clause_op")]
    [InlineData("currency", "like", "S%", "clause_op")]
    [InlineData("vendor", "eq", "not-a-guid", "clause_value")]
    [InlineData("dimension:", "eq", "x", "clause_field")]
    public void Clauses_use_only_the_closed_expression_set(string field, string op, string value, string code)
    {
        var def = Def("c", Condition("c", null, When("a", Clause(field, op, value))), Single("a", null, A));
        Assert.Contains(code, Codes(def));
    }

    [Fact]
    public void Condition_steps_need_cases_with_clauses_and_a_wrong_value_count_is_rejected()
    {
        Assert.Contains("cases_invalid", Codes(Def("c", Condition("c", null))));
        Assert.Contains("case_empty", Codes(Def("c", Condition("c", null, When("a")), Single("a", null, A))));
        Assert.Contains("clause_value", Codes(Def("c", Condition("c", null, When("a", Clause("currency", ConditionOp.Eq, "SAR", "USD"))), Single("a", null, A))));
    }

    [Fact]
    public void A_definition_round_trips_through_json()
    {
        var def = Def("p", Parallel("p", "c", JoinMode.Any, "a", "b"), Single("a", null, A), Single("b", null, B),
            Condition("c", null, When("e", Clause("amount", ConditionOp.Gt, "10"))), Single("e", null, C));
        var back = DefinitionJson.Parse(DefinitionJson.ToJson(def));

        Assert.Empty(DefinitionValidator.Validate(back));
        Assert.Equal(def.Steps.Count, back.Steps.Count);
        Assert.Equal(JoinMode.Any, back.Find("p")!.Join);
        Assert.Equal(ConditionOp.Gt, back.Find("c")!.Cases[0].All[0].Op);
    }

    [Fact]
    public void Running_an_invalid_definition_is_refused()
    {
        var bad = Def("a", Single("a", "b", A), Single("b", "a", B));
        Assert.Throws<Vpms.Workflow.Domain.Errors.WorkflowDefinitionException>(() =>
            WorkflowRun.Start(bad, Facts(), [Maker], Maker, Resolver(), DateTimeOffset.UnixEpoch));
    }
}
