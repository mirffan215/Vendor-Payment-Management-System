using Microsoft.Extensions.DependencyInjection;
using Vpms.BuildingBlocks.Security;
using Vpms.Invoices.Domain;
using Vpms.TestSupport;
using Vpms.Workflow.Application;
using Vpms.Workflow.Domain;
using static Vpms.IntegrationTests.WorkflowWorld;

namespace Vpms.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class DeadlineTests(SqlServerFixture sql) : ApprovalScenarioBase(sql)
{
    private static StepSpec Step(string mode = ApprovalMode.AnyOne, ApproverSpec? escalate = null, int due = 48, int? remind = 24, params Guid[] approvers) =>
        new() { Id = "s", Mode = mode, Approvers = approvers.Select(User).ToList(), DueHours = due, ReminderHours = remind, EscalateTo = escalate };

    private Task PublishStep(StepSpec step) => Publish(new WorkflowDefinitionSpec { Name = "Deadline", Start = "s", Steps = [step] });

    [Fact]
    public async Task A_reminder_is_sent_once_to_each_approver_who_has_not_decided()
    {
        await PublishStep(Step(ApprovalMode.All, null, 48, 24, A1, A2));
        var task = await SentInvoiceTask(A1);
        await Decide(A1, task);

        Assert.Equal(new TimerRunResult(0, 0), await RunTimers());
        Clock.Advance(TimeSpan.FromHours(24));
        Assert.Equal(new TimerRunResult(1, 0), await RunTimers());

        Assert.Empty(await Notices(A1));                       // already decided
        var notice = (await Notices(A2)).Single();
        Assert.Equal(NoticeKind.Reminder, notice.Kind);
        Assert.Contains("INV-1", notice.SubjectLabel);

        Assert.Equal(new TimerRunResult(0, 0), await RunTimers());   // never again
        Assert.Single(await Notices(A2));
        Assert.Equal(1, await AuditCount("approval.reminder-sent"));
    }

    [Fact]
    public async Task An_overdue_task_escalates_once_and_the_escalation_approver_can_complete_the_step()
    {
        await PublishStep(Step(ApprovalMode.All, User(A3), 48, 24, A1, A2));
        var invoice = await VerifiedInvoice(Clerk);
        await Send(Clerk, invoice.Id);
        Clock.Advance(TimeSpan.FromHours(48));

        var result = await RunTimers();
        Assert.Equal(1, result.Escalations);
        Assert.Equal(new TimerRunResult(0, 0), await RunTimers());

        var item = (await Inbox(A3)).Single();
        Assert.True(item.Escalated);
        Assert.True(item.Overdue);
        Assert.Equal(NoticeKind.Escalated, (await Notices(A3)).Single().Kind);
        Assert.Contains(await Notices(A1), n => n.Kind == NoticeKind.EscalatedAway);
        Assert.Equal(1, await AuditCount("approval.escalated"));

        // a mode-All step normally needs A1 and A2; one approval from the escalation approver completes it
        var run = await Decide(A3, item.TaskId);
        Assert.Equal(RunStatus.Approved, run.Status);
        Assert.Equal([A3], run.Tasks.Single().EscalatedTo);
        Assert.Equal(InvoiceStatus.Approved, (await Invoice(invoice.Id)).Status);
    }

    [Fact]
    public async Task The_original_approvers_can_still_decide_after_escalation()
    {
        await PublishStep(Step(ApprovalMode.AnyOne, User(A3), 48, null, A1));
        var task = await SentInvoiceTask(A1);
        Clock.Advance(TimeSpan.FromHours(49));
        await RunTimers();

        Assert.Single(await Inbox(A1));
        Assert.Equal(RunStatus.Approved, (await Decide(A1, task)).Status);
    }

    [Fact]
    public async Task Escalation_never_adds_the_maker_or_someone_who_already_decided()
    {
        await PublishStep(Step(ApprovalMode.All, User(Dual), 48, null, A1, A2));
        var invoice = await VerifiedInvoice(Dual);
        await Send(Dual, invoice.Id);
        Clock.Advance(TimeSpan.FromHours(48));
        await RunTimers();

        Assert.Empty(await Inbox(Dual));
        Assert.Equal(1, await AuditCount("approval.escalation-failed"));
        Assert.Equal(0, await AuditCount("approval.escalated"));
        Assert.Empty((await Runs(invoice.Id)).Single().Tasks.Single().EscalatedTo!);
        Assert.Equal(new TimerRunResult(0, 0), await RunTimers());
    }

    [Fact]
    public async Task A_step_without_an_escalation_approver_just_becomes_overdue()
    {
        await PublishStep(Step(ApprovalMode.AnyOne, null, 48, null, A1));
        await SentInvoiceTask(A1);
        Assert.False((await Inbox(A1)).Single().Overdue);
        Clock.Advance(TimeSpan.FromHours(48));
        Assert.True((await Inbox(A1)).Single().Overdue);

        await RunTimers();
        Assert.Equal(1, await AuditCount("approval.overdue"));
        Assert.Empty(await Notices(A3));
    }

    [Fact]
    public async Task Finished_approvals_get_no_reminders_or_escalations()
    {
        await PublishStep(Step(ApprovalMode.AnyOne, User(A3), 48, 24, A1));
        var task = await SentInvoiceTask(A1);
        await Decide(A1, task, DecisionKind.Reject, "no");
        Clock.Advance(TimeSpan.FromDays(10));

        Assert.Equal(new TimerRunResult(0, 0), await RunTimers());
        Assert.Empty(await Notices(A1));
        Assert.Empty(await Notices(A3));
    }

    [Fact]
    public async Task The_scan_names_only_tenants_with_work_due()
    {
        await PublishStep(Step(ApprovalMode.AnyOne, null, 48, 24, A1));
        await SentInvoiceTask(A1);
        Task<IReadOnlyList<Guid>> Scan() => W.AsAsync(W.TenantA, W.Admin, s => s.GetRequiredService<IWorkflowTimers>().TenantsWithDueWorkAsync());

        Assert.Empty(await Scan());
        Clock.Advance(TimeSpan.FromHours(25));
        Assert.Equal([W.TenantA], await Scan());
        await RunTimers();
        Assert.Empty(await Scan());
        Clock.Advance(TimeSpan.FromHours(24));
        Assert.Equal([W.TenantA], await Scan());
    }

    [Fact]
    public async Task Notices_belong_to_their_recipient_and_can_be_marked_read()
    {
        await PublishStep(Step(ApprovalMode.AnyOne, null, 48, 24, A1));
        await SentInvoiceTask(A1);
        Clock.Advance(TimeSpan.FromHours(24));
        await RunTimers();

        var notice = (await Notices(A1)).Single();
        Assert.Empty(await Notices(A2));
        await Assert.ThrowsAsync<Vpms.BuildingBlocks.Errors.NotFoundException>(() => W.AsAsync(W.TenantA, A2, async s => { await s.GetRequiredService<IWorkflowTimers>().MarkNoticeReadAsync(notice.Id); return 0; }));
        await W.AsAsync(W.TenantA, A1, async s => { await s.GetRequiredService<IWorkflowTimers>().MarkNoticeReadAsync(notice.Id); return 0; });
        Assert.Empty(await W.AsAsync(W.TenantA, A1, s => s.GetRequiredService<IWorkflowTimers>().NoticesAsync(true)));
        Assert.NotNull((await Notices(A1)).Single().ReadAt);
        Assert.Empty(await W.AsAsync(W.TenantB, A1, s => s.GetRequiredService<IWorkflowTimers>().NoticesAsync(false)));
    }

    [Fact]
    public async Task Deadlines_are_set_on_the_task_from_the_pinned_definition()
    {
        await PublishStep(Step(ApprovalMode.AnyOne, null, 72, 36, A1));
        var invoice = await VerifiedInvoice(Clerk);
        var started = Clock.GetUtcNow();
        await Send(Clerk, invoice.Id);
        var item = (await Inbox(A1)).Single();
        Assert.Equal(started.AddHours(72), item.DueAt);
    }
}
