using Vpms.BuildingBlocks.Errors;
using Vpms.BuildingBlocks.Security;
using Vpms.Workflow.Domain;
using static Vpms.UnitTests.Workflow.WorkflowKit;

namespace Vpms.UnitTests.Workflow;

/// <summary>Self-approval, different-person, delegation and approval-limit rules enforced by the engine itself.</summary>
public class WorkflowControlsTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 9, 0, 0, TimeSpan.Zero);

    private static (WorkflowDefinitionSpec Def, WorkflowRun Run) Start(WorkflowDefinitionSpec def, params Guid[] makers) =>
        (def, WorkflowRun.Start(def, Facts(), makers, makers.FirstOrDefault(), Resolver(), Now));

    private static void Decide(WorkflowDefinitionSpec def, WorkflowRun run, string step, Guid actor, Guid? actingFor = null, Func<Guid, bool>? authority = null) =>
        run.Decide(def, run.PendingTasks.First(t => t.StepId == step).Id,
            new DecisionRequest(actor, DecisionKind.Approve, null, Now, actingFor, authority), Resolver());

    [Fact]
    public void The_maker_cannot_approve_even_when_named_as_the_approver()
    {
        var (def, run) = Start(Def("s", Approval("s", null, ApprovalMode.AnyOne, 0, User(Maker), User(A))), Maker);

        Assert.DoesNotContain(Maker, run.Tasks[0].Assignees);
        var ex = Assert.Throws<SegregationOfDutiesException>(() => Decide(def, run, "s", Maker));
        Assert.Equal("maker-checker", ex.Rule);
    }

    [Fact]
    public void Every_person_who_prepared_the_invoice_is_blocked()
    {
        var creator = Guid.NewGuid();
        var editor = Guid.NewGuid();
        var (def, run) = Start(Def("s", Approval("s", null, ApprovalMode.AnyOne, 0, User(creator), User(editor), User(A))), creator, editor);

        Assert.Equal([A], run.Tasks[0].Assignees);
        Assert.Throws<SegregationOfDutiesException>(() => Decide(def, run, "s", creator));
        Assert.Throws<SegregationOfDutiesException>(() => Decide(def, run, "s", editor));
        Decide(def, run, "s", A);
        Assert.Equal(RunStatus.Approved, run.Status);
    }

    [Fact]
    public void The_maker_cannot_approve_by_standing_in_for_an_approver()
    {
        var (def, run) = Start(Def("s", Single("s", null, A)), Maker);
        Assert.Throws<SegregationOfDutiesException>(() => Decide(def, run, "s", Maker, actingFor: A));
    }

    [Fact]
    public void Nobody_can_approve_on_behalf_of_the_maker_either()
    {
        // B is a delegate of the maker; the maker cannot be an approver, so the delegation gives B nothing.
        var (def, run) = Start(Def("s", Approval("s", null, ApprovalMode.AnyOne, 0, User(Maker), User(A))), Maker);
        var ex = Assert.Throws<SegregationOfDutiesException>(() => Decide(def, run, "s", B, actingFor: Maker));
        Assert.Equal("maker-checker", ex.Rule);
    }

    [Fact]
    public void A_user_who_is_not_an_approver_is_refused()
    {
        var (def, run) = Start(Def("s", Single("s", null, A)), Maker);
        var ex = Assert.Throws<ValidationFailedException>(() => Decide(def, run, "s", B));
        Assert.Equal("not_an_approver", ex.Code);
        Assert.Equal(403, ex.StatusCode);
    }

    [Fact]
    public void One_person_cannot_decide_two_parallel_branches()
    {
        var def = Def("p", Parallel("p", null, JoinMode.All, "b1", "b2"),
            Approval("b1", null, ApprovalMode.AnyOne, 0, User(A), User(B)), Approval("b2", null, ApprovalMode.AnyOne, 0, User(A), User(C)));
        var (_, run) = Start(def, Maker);

        Decide(def, run, "b1", A);
        var ex = Assert.Throws<SegregationOfDutiesException>(() => Decide(def, run, "b2", A));
        Assert.Equal("different-person", ex.Rule);
        Decide(def, run, "b2", C);
        Assert.Equal(RunStatus.Approved, run.Status);
    }

    [Fact]
    public void The_same_person_never_gets_a_second_sequential_step()
    {
        var def = Def("l1", Single("l1", "l2", A), Approval("l2", null, ApprovalMode.AnyOne, 0, User(A), User(B)));
        var (_, run) = Start(def, Maker);
        Decide(def, run, "l1", A);
        Assert.Equal([B], run.PendingTasks.Single().Assignees);
        Assert.Throws<SegregationOfDutiesException>(() => Decide(def, run, "l2", A));
    }

    [Fact]
    public void An_approver_cannot_decide_the_same_all_mode_task_twice()
    {
        var def = Def("s", Approval("s", null, ApprovalMode.All, 0, User(A), User(B)));
        var (_, run) = Start(def, Maker);
        Decide(def, run, "s", A);
        Assert.Throws<SegregationOfDutiesException>(() => Decide(def, run, "s", A));
        Assert.Equal(RunStatus.Active, run.Status);
    }

    [Fact]
    public void A_delegate_decides_for_the_approver_and_is_recorded()
    {
        var (def, run) = Start(Def("s", Single("s", null, A)), Maker);
        Decide(def, run, "s", B, actingFor: A);

        var d = run.Tasks[0].Decisions.Single();
        Assert.Equal(B, d.ActorId);
        Assert.Equal(A, d.ActingForId);
        Assert.Equal(RunStatus.Approved, run.Status);
    }

    [Fact]
    public void A_delegate_cannot_decide_a_step_the_delegator_cannot()
    {
        var (def, run) = Start(Def("s", Single("s", null, A)), Maker);
        Assert.Throws<ValidationFailedException>(() => Decide(def, run, "s", B, actingFor: C));
    }

    [Fact]
    public void After_a_delegate_decides_the_delegator_cannot_decide_a_later_step()
    {
        var def = Def("l1", Single("l1", "l2", A), Approval("l2", null, ApprovalMode.AnyOne, 0, User(A), User(C)));
        var (_, run) = Start(def, Maker);
        Decide(def, run, "l1", B, actingFor: A);

        Assert.Equal([C], run.PendingTasks.Single().Assignees);
        Assert.Throws<SegregationOfDutiesException>(() => Decide(def, run, "l2", A));
    }

    [Fact]
    public void A_delegate_who_already_decided_cannot_decide_again_for_someone_else()
    {
        var def = Def("p", Parallel("p", null, JoinMode.All, "b1", "b2"), Single("b1", null, A), Single("b2", null, C));
        var (_, run) = Start(def, Maker);
        Decide(def, run, "b1", B, actingFor: A);
        Assert.Throws<SegregationOfDutiesException>(() => Decide(def, run, "b2", B, actingFor: C));
    }

    [Fact]
    public void The_approval_limit_applies_to_the_person_whose_authority_is_used()
    {
        var (def, run) = Start(Def("s", Single("s", null, A)), Maker);

        var ex = Assert.Throws<ValidationFailedException>(() => Decide(def, run, "s", B, actingFor: A, authority: u => u != A));
        Assert.Equal("approval_limit_exceeded", ex.Code);
        Assert.Equal(RunStatus.Active, run.Status);
        Assert.Empty(run.Tasks[0].Decisions);

        Decide(def, run, "s", B, actingFor: A, authority: u => u == A);
        Assert.Equal(RunStatus.Approved, run.Status);
    }

    [Fact]
    public void The_limit_does_not_stop_a_rejection()
    {
        var (def, run) = Start(Def("s", Single("s", null, A)), Maker);
        run.Decide(def, run.Tasks[0].Id, new DecisionRequest(A, DecisionKind.Reject, "no", Now, null, _ => false), Resolver());
        Assert.Equal(RunStatus.Rejected, run.Status);
    }
}
