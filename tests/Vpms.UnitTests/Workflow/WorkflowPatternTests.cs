using Vpms.BuildingBlocks.Errors;
using Vpms.BuildingBlocks.Security;
using Vpms.Workflow.Domain;
using static Vpms.UnitTests.Workflow.WorkflowKit;

namespace Vpms.UnitTests.Workflow;

/// <summary>One simulation per workflow pattern: sequential, parallel (join all / any), conditional, multilevel, nested.</summary>
public class WorkflowPatternTests
{
    private static SimResult Run(WorkflowDefinitionSpec def, SubjectFacts facts, params SimAction[] script) =>
        WorkflowSimulator.Run(def, facts, [Maker], Resolver(), script);

    [Fact]
    public void Sequential_two_levels_approve_in_order()
    {
        var def = Def("l1", Single("l1", "l2", A), Single("l2", null, B));

        var r = Run(def, Facts(), new SimAction(A));
        Assert.Equal(RunStatus.Active, r.Run.Status);
        Assert.Equal(["l2"], r.Run.PendingTasks.Select(t => t.StepId));

        r = Run(def, Facts(), new SimAction(A), new SimAction(B));
        Assert.Equal(RunStatus.Approved, r.Run.Status);
    }

    [Fact]
    public void Sequential_second_level_cannot_be_decided_before_the_first()
    {
        var def = Def("l1", Single("l1", "l2", A), Single("l2", null, B));
        Assert.Throws<InvalidOperationException>(() => Run(def, Facts(), new SimAction(B)));
    }

    [Fact]
    public void Parallel_join_all_waits_for_every_branch()
    {
        var def = Def("p", Parallel("p", "end", JoinMode.All, "b1", "b2"), Single("b1", null, A), Single("b2", null, B), Single("end", null, C));

        var r = Run(def, Facts(), new SimAction(A));
        Assert.Equal(["b2"], r.Run.PendingTasks.Select(t => t.StepId));

        r = Run(def, Facts(), new SimAction(A), new SimAction(B));
        Assert.Equal(["end"], r.Run.PendingTasks.Select(t => t.StepId));

        r = Run(def, Facts(), new SimAction(A), new SimAction(B), new SimAction(C));
        Assert.Equal(RunStatus.Approved, r.Run.Status);
    }

    [Fact]
    public void Parallel_join_any_continues_on_first_branch_and_cancels_the_rest()
    {
        var def = Def("p", Parallel("p", "end", JoinMode.Any, "b1", "b2"), Single("b1", null, A), Single("b2", null, B), Single("end", null, C));

        var r = Run(def, Facts(), new SimAction(B));
        Assert.Equal(["end"], r.Run.PendingTasks.Select(t => t.StepId));
        Assert.Contains(r.Run.Tasks, t => t.StepId == "b1" && t.Status == Vpms.Workflow.Domain.TaskStatus.Cancelled);

        r = Run(def, Facts(), new SimAction(B), new SimAction(C));
        Assert.Equal(RunStatus.Approved, r.Run.Status);
    }

    [Fact]
    public void Parallel_branches_can_be_multilevel_and_the_workflow_can_end_at_the_join()
    {
        var def = Def("p", Parallel("p", null, JoinMode.All, "b1a", "b2"),
            Single("b1a", "b1b", A), Single("b1b", null, B), Single("b2", null, C));

        var r = Run(def, Facts(), new SimAction(A), new SimAction(C));
        Assert.Equal(["b1b"], r.Run.PendingTasks.Select(t => t.StepId));
        r = Run(def, Facts(), new SimAction(A), new SimAction(C), new SimAction(B));
        Assert.Equal(RunStatus.Approved, r.Run.Status);
    }

    [Fact]
    public void Nested_parallel_joins_independently()
    {
        var def = Def("outer", Parallel("outer", "end", JoinMode.All, "x", "inner"),
            Single("x", null, A),
            Parallel("inner", null, JoinMode.All, "y1", "y2"),
            Single("y1", null, B), Single("y2", null, C), Single("end", null, D));

        var r = Run(def, Facts(), new SimAction(B), new SimAction(C), new SimAction(A));
        Assert.Equal(["end"], r.Run.PendingTasks.Select(t => t.StepId));
        r = Run(def, Facts(), new SimAction(B), new SimAction(C), new SimAction(A), new SimAction(D));
        Assert.Equal(RunStatus.Approved, r.Run.Status);

        r = Run(def, Facts(), new SimAction(A), new SimAction(B));
        Assert.DoesNotContain(r.Run.PendingTasks, t => t.StepId == "end");
    }

    [Theory]
    [InlineData(500, "small")]
    [InlineData(10_000, "large")]
    [InlineData(10_000.01, "large")]
    [InlineData(9_999.99, "small")]
    public void Condition_routes_by_amount(double amount, string expected)
    {
        var def = Def("c", Condition("c", "small", When("large", Clause("amount", ConditionOp.Ge, "10000"))),
            Single("small", null, A), Single("large", null, B));

        var r = Run(def, Facts((decimal)amount));
        Assert.Equal([expected], r.Run.PendingTasks.Select(t => t.StepId));
    }

    [Fact]
    public void Condition_matches_first_case_and_can_end_the_path_by_default()
    {
        var vendor = Guid.NewGuid();
        var def = Def("c", Condition("c", Targets.End,
                When("vip", Clause("vendor", ConditionOp.Eq, vendor.ToString())),
                When("cost", Clause("dimension:CostCenter", ConditionOp.In, "CC1", "CC2"), Clause("currency", ConditionOp.Eq, "SAR"))),
            Single("vip", null, A), Single("cost", null, B));

        var dims = new Dictionary<string, string> { ["CostCenter"] = "cc2" };
        Assert.Equal(["vip"], WorkflowSimulator.Run(def, SubjectFacts.Of(1, "SAR", vendor, "Manual", dims), [Maker], Resolver(), []).Run.PendingTasks.Select(t => t.StepId));
        Assert.Equal(["cost"], WorkflowSimulator.Run(def, SubjectFacts.Of(1, "SAR", null, "Manual", dims), [Maker], Resolver(), []).Run.PendingTasks.Select(t => t.StepId));
        Assert.Equal(RunStatus.Approved, WorkflowSimulator.Run(def, SubjectFacts.Of(1, "USD", null, "Manual", dims), [Maker], Resolver(), []).Run.Status);
    }

    [Fact]
    public void A_condition_with_no_match_and_no_default_is_an_error_not_an_approval()
    {
        var def = Def("c", Condition("c", null, When("a", Clause("amount", ConditionOp.Gt, "1000"))), Single("a", null, A));
        var ex = Assert.Throws<ConflictException>(() => Run(def, Facts(10)));
        Assert.Equal("no_route", ex.Code);
    }

    [Fact]
    public void Condition_diamond_converges_on_a_shared_step()
    {
        var def = Def("c", Condition("c", "a", When("b", Clause("amount", ConditionOp.Gt, "50"))),
            Single("a", "final", A), Single("b", "final", B), Single("final", null, C));

        var r = Run(def, Facts(10), new SimAction(A));
        Assert.Equal(["final"], r.Run.PendingTasks.Select(t => t.StepId));
        r = Run(def, Facts(99), new SimAction(B), new SimAction(C));
        Assert.Equal(RunStatus.Approved, r.Run.Status);
    }

    [Fact]
    public void Multilevel_by_amount_adds_levels_through_conditions()
    {
        var def = Def("l1", Single("l1", "gate", A),
            Condition("gate", Targets.End, When("l2", Clause("amount", ConditionOp.Gt, "1000"))),
            Single("l2", "gate2", B),
            Condition("gate2", Targets.End, When("l3", Clause("amount", ConditionOp.Gt, "50000"))),
            Single("l3", null, C));

        Assert.Equal(RunStatus.Approved, Run(def, Facts(900), new SimAction(A)).Run.Status);
        Assert.Equal(RunStatus.Approved, Run(def, Facts(5000), new SimAction(A), new SimAction(B)).Run.Status);
        Assert.Equal(["l3"], Run(def, Facts(60000), new SimAction(A), new SimAction(B)).Run.PendingTasks.Select(t => t.StepId));
    }

    [Fact]
    public void Approval_modes_AnyOne_All_and_Quorum()
    {
        var any = Def("s", Approval("s", null, ApprovalMode.AnyOne, 0, User(A), User(B)));
        Assert.Equal(RunStatus.Approved, Run(any, Facts(), new SimAction(B)).Run.Status);

        var all = Def("s", Approval("s", null, ApprovalMode.All, 0, User(A), User(B)));
        Assert.Equal(RunStatus.Active, Run(all, Facts(), new SimAction(A)).Run.Status);
        Assert.Equal(RunStatus.Approved, Run(all, Facts(), new SimAction(A), new SimAction(B)).Run.Status);

        var quorum = Def("s", Approval("s", null, ApprovalMode.Quorum, 2, User(A), User(B), User(C)));
        Assert.Equal(RunStatus.Active, Run(quorum, Facts(), new SimAction(A)).Run.Status);
        Assert.Equal(RunStatus.Approved, Run(quorum, Facts(), new SimAction(A), new SimAction(C)).Run.Status);
    }

    [Fact]
    public void Role_approvers_are_resolved_to_the_current_holders()
    {
        var def = Def("s", Approval("s", null, ApprovalMode.AnyOne, 0, Role("Finance Manager")));
        var resolver = Resolver(new() { ["Finance Manager"] = [A, B] });

        var r = WorkflowSimulator.Run(def, Facts(), [Maker], resolver, [new SimAction(B)]);
        Assert.Equal(RunStatus.Approved, r.Run.Status);
    }

    [Fact]
    public void Reject_and_return_end_the_run_and_cancel_open_tasks()
    {
        var def = Def("p", Parallel("p", null, JoinMode.All, "b1", "b2"), Single("b1", null, A), Single("b2", null, B));

        var rejected = Run(def, Facts(), new SimAction(A, DecisionKind.Reject, "wrong vendor"));
        Assert.Equal(RunStatus.Rejected, rejected.Run.Status);
        Assert.Equal("wrong vendor", rejected.Run.StatusReason);
        Assert.Empty(rejected.Run.PendingTasks);

        var returned = Run(def, Facts(), new SimAction(B, DecisionKind.Return, "please attach PO"));
        Assert.Equal(RunStatus.Returned, returned.Run.Status);
    }

    [Fact]
    public void Reject_and_return_need_a_comment()
    {
        var def = Def("s", Single("s", null, A));
        var run = WorkflowRun.Start(def, Facts(), [Maker], Maker, Resolver(), DateTimeOffset.UnixEpoch);
        var ex = Assert.Throws<ValidationFailedException>(() =>
            run.Decide(def, run.Tasks[0].Id, new DecisionRequest(A, DecisionKind.Reject, "  ", DateTimeOffset.UnixEpoch), Resolver()));
        Assert.Equal("comment_required", ex.Code);
    }

    [Fact]
    public void A_finished_run_cannot_be_decided_again()
    {
        var def = Def("s", Single("s", null, A));
        var run = WorkflowRun.Start(def, Facts(), [Maker], Maker, Resolver(), DateTimeOffset.UnixEpoch);
        var task = run.Tasks[0];
        run.Decide(def, task.Id, new DecisionRequest(A, DecisionKind.Approve, null, DateTimeOffset.UnixEpoch), Resolver());

        var ex = Assert.Throws<ConflictException>(() =>
            run.Decide(def, task.Id, new DecisionRequest(A, DecisionKind.Approve, null, DateTimeOffset.UnixEpoch), Resolver()));
        Assert.Equal("run_not_active", ex.Code);
    }

    [Fact]
    public void Preview_walks_the_route_when_everyone_approves()
    {
        var def = Def("c", Condition("c", "a", When("b", Clause("amount", ConditionOp.Gt, "50"))),
            Single("a", null, A), Single("b", "b2", B), Single("b2", null, C));

        var preview = WorkflowSimulator.Preview(def, Facts(99), [Maker], Resolver());
        Assert.Equal(RunStatus.Approved, preview.Run.Status);
        Assert.Equal(["b", "b2"], preview.Run.Tasks.Select(t => t.StepId));
    }

    [Fact]
    public void Deadlines_are_set_from_the_step()
    {
        var step = new StepSpec { Id = "s", Approvers = [User(A)], DueHours = 48, ReminderHours = 24 };
        var def = Def("s", step);
        var now = new DateTimeOffset(2026, 3, 1, 8, 0, 0, TimeSpan.Zero);
        var run = WorkflowRun.Start(def, Facts(), [Maker], Maker, Resolver(), now);

        Assert.Equal(now.AddHours(48), run.Tasks[0].DueAt);
        Assert.Equal(now.AddHours(24), run.Tasks[0].ReminderAt);
    }

    [Fact]
    public void Cancel_ends_an_active_run()
    {
        var def = Def("s", Single("s", null, A));
        var run = WorkflowRun.Start(def, Facts(), [Maker], Maker, Resolver(), DateTimeOffset.UnixEpoch);
        run.Cancel("invoice changed", DateTimeOffset.UnixEpoch);
        Assert.Equal(RunStatus.Cancelled, run.Status);
        Assert.Empty(run.PendingTasks);
        Assert.Throws<ConflictException>(() => run.Cancel("again", DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public void The_first_step_needs_an_eligible_approver()
    {
        var def = Def("s", Single("s", null, Maker));
        var ex = Assert.Throws<ValidationFailedException>(() => WorkflowRun.Start(def, Facts(), [Maker], Maker, Resolver(), DateTimeOffset.UnixEpoch));
        Assert.Equal("no_eligible_approver", ex.Code);
    }

    [Fact]
    public void A_later_step_with_nobody_eligible_needs_attention_instead_of_failing_the_decision()
    {
        var def = Def("l1", Single("l1", "l2", A), Single("l2", null, A));
        var r = Run(def, Facts(), new SimAction(A));
        Assert.True(r.Run.NeedsAttention);
        Assert.Equal(RunStatus.Active, r.Run.Status);
    }
}
