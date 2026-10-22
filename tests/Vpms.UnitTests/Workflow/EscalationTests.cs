using Vpms.BuildingBlocks.Errors;
using Vpms.Workflow.Domain;
using static Vpms.UnitTests.Workflow.WorkflowKit;

namespace Vpms.UnitTests.Workflow;

public class EscalationTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 9, 0, 0, TimeSpan.Zero);

    private static StepSpec Step(string mode, params Guid[] approvers) => new()
    {
        Id = "s", Mode = mode, Approvers = approvers.Select(User).ToList(), DueHours = 24, EscalateTo = User(D),
    };

    [Fact]
    public void Escalation_adds_the_escalation_approver_once()
    {
        var def = Def("s", Step(ApprovalMode.AnyOne, A));
        var run = WorkflowRun.Start(def, Facts(), [Maker], Maker, Resolver(), Now);

        Assert.Equal([D], run.Escalate(def, run.Tasks[0].Id, Resolver(), Now.AddHours(24)));
        Assert.Equal([A, D], run.Tasks[0].Assignees);
        Assert.Empty(run.Escalate(def, run.Tasks[0].Id, Resolver(), Now.AddHours(25)));
        Assert.Equal([A, D], run.Tasks[0].Assignees);
    }

    [Fact]
    public void One_approval_from_the_escalation_approver_completes_an_all_mode_step()
    {
        var def = Def("s", Step(ApprovalMode.All, A, B, C));
        var run = WorkflowRun.Start(def, Facts(), [Maker], Maker, Resolver(), Now);
        run.Escalate(def, run.Tasks[0].Id, Resolver(), Now);

        run.Decide(def, run.Tasks[0].Id, new DecisionRequest(D, DecisionKind.Approve, null, Now), Resolver());
        Assert.Equal(RunStatus.Approved, run.Status);
    }

    [Fact]
    public void Without_escalation_an_all_mode_step_still_needs_everyone()
    {
        var def = Def("s", Step(ApprovalMode.All, A, B));
        var run = WorkflowRun.Start(def, Facts(), [Maker], Maker, Resolver(), Now);
        run.Decide(def, run.Tasks[0].Id, new DecisionRequest(A, DecisionKind.Approve, null, Now), Resolver());
        Assert.Equal(RunStatus.Active, run.Status);
    }

    [Fact]
    public void The_maker_and_earlier_deciders_are_not_escalation_candidates()
    {
        var def = Def("l1", Single("l1", "s", A), new StepSpec { Id = "s", Approvers = [User(B)], DueHours = 24, EscalateTo = User(A) });
        var run = WorkflowRun.Start(def, Facts(), [Maker], Maker, Resolver(), Now);
        run.Decide(def, run.Tasks[0].Id, new DecisionRequest(A, DecisionKind.Approve, null, Now), Resolver());

        Assert.Empty(run.Escalate(def, run.PendingTasks.Single().Id, Resolver(), Now));
        Assert.NotNull(run.PendingTasks.Single().EscalatedAt);

        var makerDef = Def("s", new StepSpec { Id = "s", Approvers = [User(B)], DueHours = 24, EscalateTo = User(Maker) });
        var run2 = WorkflowRun.Start(makerDef, Facts(), [Maker], Maker, Resolver(), Now);
        Assert.Empty(run2.Escalate(makerDef, run2.Tasks[0].Id, Resolver(), Now));
    }

    [Fact]
    public void A_step_without_an_escalation_approver_only_records_that_it_is_overdue()
    {
        var def = Def("s", new StepSpec { Id = "s", Approvers = [User(A)], DueHours = 24 });
        var run = WorkflowRun.Start(def, Facts(), [Maker], Maker, Resolver(), Now);
        Assert.Empty(run.Escalate(def, run.Tasks[0].Id, Resolver(), Now));
        Assert.Equal([A], run.Tasks[0].Assignees);
        Assert.Equal(Now, run.Tasks[0].EscalatedAt);
    }

    [Fact]
    public void A_finished_task_cannot_be_escalated()
    {
        var def = Def("s", Step(ApprovalMode.AnyOne, A));
        var run = WorkflowRun.Start(def, Facts(), [Maker], Maker, Resolver(), Now);
        var id = run.Tasks[0].Id;
        run.Decide(def, id, new DecisionRequest(A, DecisionKind.Approve, null, Now), Resolver());
        Assert.Throws<ConflictException>(() => run.Escalate(def, id, Resolver(), Now));
    }

    [Fact]
    public void Delegation_windows_are_half_open()
    {
        var d = new Delegation { Status = DelegationStatus.Active, ValidFrom = Now, ValidTo = Now.AddDays(1) };
        Assert.False(d.CoversAt(Now.AddTicks(-1)));
        Assert.True(d.CoversAt(Now));
        Assert.True(d.CoversAt(Now.AddDays(1).AddTicks(-1)));
        Assert.False(d.CoversAt(Now.AddDays(1)));
        d.Revoke(A, Now);
        Assert.False(d.CoversAt(Now));
    }
}
