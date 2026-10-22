using Microsoft.Extensions.DependencyInjection;
using Vpms.BuildingBlocks.Errors;
using Vpms.BuildingBlocks.Security;
using Vpms.Invoices.Domain;
using Vpms.TestSupport;
using Vpms.Workflow.Application;
using Vpms.Workflow.Domain;
using static Vpms.IntegrationTests.WorkflowWorld;

namespace Vpms.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class DelegationTests(SqlServerFixture sql) : ApprovalScenarioBase(sql)
{
    private Task<WorkflowDefinitionSpec> OneStep(params Guid[] approvers) => Task.FromResult(new WorkflowDefinitionSpec
    {
        Name = "One", Start = "s", Steps = [new StepSpec { Id = "s", Approvers = approvers.Select(User).ToList() }],
    });

    [Fact]
    public async Task A_delegate_sees_the_delegators_task_and_decides_it_acting_for_them()
    {
        await Publish(await OneStep(A1));
        await Delegate(A1, A2);
        var invoice = await VerifiedInvoice(Clerk);
        await Send(Clerk, invoice.Id);

        var item = (await Inbox(A2)).Single();
        Assert.Equal(A1, item.ActingForId);
        Assert.Equal("a1@t.com", item.ActingForName);
        Assert.Single(await Inbox(A1)); // the delegator keeps their own task too

        var run = await Decide(A2, item.TaskId, comment: "covering for a1", actingFor: A1);
        Assert.Equal(RunStatus.Approved, run.Status);
        var decision = run.Tasks.Single().Decisions.Single();
        Assert.Equal(A2, decision.ActorId);
        Assert.Equal(A1, decision.ActingForId);
        Assert.Equal("a2@t.com", decision.ActorName);
        Assert.Equal("a1@t.com", decision.ActingForName);
        Assert.Equal(InvoiceStatus.Approved, (await Invoice(invoice.Id)).Status);
        Assert.Empty(await Inbox(A1));
        Assert.Equal(1, await AuditCount("approval.approve"));
    }

    [Fact]
    public async Task Without_a_delegation_nobody_can_act_for_an_approver()
    {
        await Publish(await OneStep(A1));
        var task = await SentInvoiceTask(A1);
        Assert.Empty(await Inbox(A2));
        var ex = await Assert.ThrowsAsync<ValidationFailedException>(() => Decide(A2, task, actingFor: A1));
        Assert.Equal("no_delegation", ex.Code);
        Assert.Equal(403, ex.StatusCode);
    }

    [Fact]
    public async Task A_delegation_only_works_inside_its_period_and_the_end_is_exclusive()
    {
        await Publish(await OneStep(A1));
        await Delegate(A1, A2, from: TimeSpan.FromDays(1), length: TimeSpan.FromDays(2));
        var task = await SentInvoiceTask(A1);

        Assert.Empty(await Inbox(A2));
        Assert.Equal("no_delegation", (await Assert.ThrowsAsync<ValidationFailedException>(() => Decide(A2, task, actingFor: A1))).Code);

        Clock.Advance(TimeSpan.FromDays(1));
        Assert.Single(await Inbox(A2));

        Clock.Advance(TimeSpan.FromDays(2) - TimeSpan.FromTicks(1));
        Assert.Single(await Inbox(A2));

        Clock.Advance(TimeSpan.FromTicks(1)); // exactly ValidTo
        Assert.Empty(await Inbox(A2));
        Assert.Equal("no_delegation", (await Assert.ThrowsAsync<ValidationFailedException>(() => Decide(A2, task, actingFor: A1))).Code);
    }

    [Fact]
    public async Task Revoking_a_delegation_takes_effect_immediately_and_either_party_may_revoke()
    {
        await Publish(await OneStep(A1));
        var d = await Delegate(A1, A2);
        var task = await SentInvoiceTask(A1);
        Assert.Single(await Inbox(A2));

        await W.AsAsync(W.TenantA, A2, s => s.GetRequiredService<IDelegationService>().RevokeAsync(d.Id, false)); // the delegate declines it
        Assert.Empty(await Inbox(A2));
        Assert.Equal("no_delegation", (await Assert.ThrowsAsync<ValidationFailedException>(() => Decide(A2, task, actingFor: A1))).Code);

        var again = await W.AsAsync(W.TenantA, A1, s => s.GetRequiredService<IDelegationService>().MineAsync(true));
        Assert.Empty(again);
        Assert.Equal("not_active", (await Assert.ThrowsAsync<ConflictException>(() => W.AsAsync(W.TenantA, A1, s => s.GetRequiredService<IDelegationService>().RevokeAsync(d.Id, false)))).Code);
    }

    [Fact]
    public async Task A_third_person_cannot_revoke_but_an_administrator_can()
    {
        var d = await Delegate(A1, A2);
        await Assert.ThrowsAsync<NotFoundException>(() => W.AsAsync(W.TenantA, A3, s => s.GetRequiredService<IDelegationService>().RevokeAsync(d.Id, false)));
        var revoked = await W.AsAsync(W.TenantA, A3, s => s.GetRequiredService<IDelegationService>().RevokeAsync(d.Id, true));
        Assert.Equal(DelegationStatus.Revoked, revoked.Status);
        Assert.Equal(DelegationStatus.Revoked, (await W.AsAsync(W.TenantA, W.Admin, s => s.GetRequiredService<IDelegationService>().ForEntityAsync(Entity))).Single(x => x.Id == d.Id).Status);
    }

    [Fact]
    public async Task An_amount_cap_limits_what_the_delegate_can_see_and_decide()
    {
        await Publish(await OneStep(A1));
        await Delegate(A1, A2, cap: 200m);
        var small = await VerifiedInvoice(Clerk, "SMALL", 100m);   // total 115
        var big = await VerifiedInvoice(Clerk, "BIG", 1000m);      // total 1150
        await Send(Clerk, small.Id);
        await Send(Clerk, big.Id);

        var seen = await Inbox(A2);
        Assert.Contains("SMALL", seen.Single().SubjectLabel);
        var bigTask = (await Inbox(A1)).Single(i => i.SubjectLabel.Contains("BIG")).TaskId;
        Assert.Equal("no_delegation", (await Assert.ThrowsAsync<ValidationFailedException>(() => Decide(A2, bigTask, actingFor: A1))).Code);
    }

    [Fact]
    public async Task A_delegate_acts_with_the_delegators_authority_not_their_own()
    {
        var limited = await W.UserAsync(W.TenantA, "limited@t.com", null, "Invoice Approver");
        await W.AuthorityAsync(W.TenantA, Entity, limited, max: 100m, from: Clock.GetUtcNow().AddDays(-1));
        await Publish(await OneStep(limited));
        await Delegate(limited, A2);                          // A2 has a large authority of their own
        var task = await SentInvoiceTask(limited, "BIGGER", 1000m);

        var ex = await Assert.ThrowsAsync<ValidationFailedException>(() => Decide(A2, task, actingFor: limited));
        Assert.Equal("approval_limit_exceeded", ex.Code);
    }

    [Fact]
    public async Task A_delegate_who_prepared_the_invoice_can_never_decide_it()
    {
        await Publish(await OneStep(A1));
        await Delegate(A1, Dual);
        var invoice = await VerifiedInvoice(Dual);
        await Send(Dual, invoice.Id);

        Assert.Empty(await Inbox(Dual));
        var task = await TaskOf(A1);
        var ex = await Assert.ThrowsAsync<SegregationOfDutiesException>(() => Decide(Dual, task, actingFor: A1));
        Assert.Equal("maker-checker", ex.Rule);
        Assert.Equal(1, await AuditCount("sod.invoice-approval.blocked"));
    }

    [Fact]
    public async Task Delegations_cannot_be_chained()
    {
        await Publish(await OneStep(A1));
        await Delegate(A1, A2);
        await Delegate(A2, A3);   // A2 passes on their own approvals, not A1's
        var task = await SentInvoiceTask(A1);

        Assert.Empty(await Inbox(A3));
        Assert.Equal("no_delegation", (await Assert.ThrowsAsync<ValidationFailedException>(() => Decide(A3, task, actingFor: A1))).Code);
        Assert.Equal("not_an_approver", (await Assert.ThrowsAsync<ValidationFailedException>(() => Decide(A3, task, actingFor: A2))).Code);
    }

    [Fact]
    public async Task The_delegate_and_the_delegator_cannot_both_decide_the_same_task()
    {
        await Publish(new WorkflowDefinitionSpec { Name = "All", Start = "s", Steps = [new StepSpec { Id = "s", Mode = ApprovalMode.All, Approvers = [User(A1), User(A3)] }] });
        await Delegate(A1, A2);
        var task = await SentInvoiceTask(A1);
        await Decide(A2, task, actingFor: A1);

        Assert.Empty(await Inbox(A1));
        await Assert.ThrowsAsync<SegregationOfDutiesException>(() => Decide(A1, task));
    }

    [Fact]
    public async Task Delegations_are_validated()
    {
        Task<DelegationDto> Make(Guid by, Guid to, TimeSpan? from = null, TimeSpan? length = null, decimal? cap = null) => Delegate(by, to, from, length, cap);
        Assert.Equal("self_delegation", (await Assert.ThrowsAsync<ValidationFailedException>(() => Make(A1, A1))).Code);
        Assert.Equal("invalid_period", (await Assert.ThrowsAsync<ValidationFailedException>(() => Make(A1, A2, length: TimeSpan.Zero))).Code);
        Assert.Equal("invalid_period", (await Assert.ThrowsAsync<ValidationFailedException>(() => Make(A1, A2, from: TimeSpan.FromDays(-10), length: TimeSpan.FromDays(5)))).Code);
        Assert.Equal("period_too_long", (await Assert.ThrowsAsync<ValidationFailedException>(() => Make(A1, A2, length: TimeSpan.FromDays(181)))).Code);
        Assert.Equal("invalid_cap", (await Assert.ThrowsAsync<ValidationFailedException>(() => Make(A1, A2, cap: 0m))).Code);
        Assert.Equal("approver_not_permitted", (await Assert.ThrowsAsync<ValidationFailedException>(() => Make(A1, Clerk))).Code);
        Assert.Equal("approver_not_permitted", (await Assert.ThrowsAsync<ValidationFailedException>(() => Make(Clerk, A1))).Code);

        await Make(A1, A2, length: TimeSpan.FromDays(10));
        Assert.Equal("delegation_overlaps", (await Assert.ThrowsAsync<ConflictException>(() => Make(A1, A2, from: TimeSpan.FromDays(5), length: TimeSpan.FromDays(10)))).Code);
        await Make(A1, A2, from: TimeSpan.FromDays(10), length: TimeSpan.FromDays(5)); // adjacent, not overlapping
        await Make(A1, A3);                                                              // a different delegate is fine
    }

    [Fact]
    public async Task Delegating_for_someone_else_needs_the_administration_permission()
    {
        var ex = await Assert.ThrowsAsync<ValidationFailedException>(() => Delegate(A1, A2, actor: A3, admin: false));
        Assert.Equal("not_allowed", ex.Code);
        var made = await Delegate(A1, A2, actor: A3, admin: true);
        Assert.Equal(A1, made.DelegatorId);
        Assert.Equal(A3, made.CreatedBy);
    }

    [Fact]
    public async Task Expired_delegations_are_reported_as_expired()
    {
        await Delegate(A1, A2, length: TimeSpan.FromDays(1));
        Clock.Advance(TimeSpan.FromDays(2));
        var mine = await W.AsAsync(W.TenantA, A1, s => s.GetRequiredService<IDelegationService>().MineAsync(false));
        Assert.Equal("Expired", mine.Single().Status);
        Assert.Empty(await W.AsAsync(W.TenantA, A1, s => s.GetRequiredService<IDelegationService>().MineAsync(true)));
    }

    [Fact]
    public async Task Delegations_are_private_to_the_tenant()
    {
        await Delegate(A1, A2);
        Assert.Empty(await W.AsAsync(W.TenantB, A1, s => s.GetRequiredService<IDelegationService>().MineAsync(false)));
        Assert.Empty(await W.AsAsync(W.TenantB, W.Admin, s => s.GetRequiredService<IDelegationService>().ForEntityAsync(Entity)));
    }
}
