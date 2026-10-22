using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Vpms.BuildingBlocks.Errors;
using Vpms.BuildingBlocks.Security;
using Vpms.Invoices.Application;
using Vpms.Invoices.Domain;
using Vpms.Invoices.Infrastructure;
using Vpms.TestSupport;
using Vpms.Workflow.Application;
using Vpms.Workflow.Contracts;
using Vpms.Workflow.Domain;
using Vpms.Workflow.Infrastructure;
using static Vpms.IntegrationTests.WorkflowWorld;

namespace Vpms.IntegrationTests;

/// <summary>Invoices travelling through the real engine, database and permissions: who is asked, who may decide, what the invoice becomes.</summary>
[Collection(SqlServerCollection.Name)]
public sealed class ApprovalTests : IAsyncLifetime
{
    private readonly SqlServerFixture _sql;
    private readonly FlakyOutcome _flaky = new();
    private WorkflowWorld _w = null!;
    private Guid _entity, _vendor, _otherEntity;
    private Guid _cfg, _chg, _clerk, _a1, _a2, _a3, _dual, _outsider;
    private Guid _maker, _checker;

    public ApprovalTests(SqlServerFixture sql) => _sql = sql;

    public async Task InitializeAsync()
    {
        var host = new IntegrationHost(await _sql.CreateDatabaseAsync(), configure: s =>
        {
            s.RemoveAll<IApprovalOutcomeHandler>();
            s.AddScoped<IApprovalOutcomeHandler>(sp => new FlakyHandler(ActivatorUtilities.CreateInstance<InvoiceApprovalOutcomeHandler>(sp), _flaky));
        });
        _w = new WorkflowWorld(host);
        await _w.InitializeAsync();
        _entity = await _w.EntityAsync(_w.TenantA, "A1");
        _otherEntity = await _w.EntityAsync(_w.TenantA, "A2");
        _cfg = await _w.UserAsync(_w.TenantA, "cfg@t.com", null, "Workflow Administrator");
        _chg = await _w.UserAsync(_w.TenantA, "chg@t.com", null, "Workflow Change Approver");
        _clerk = await _w.UserAsync(_w.TenantA, "clerk@t.com", null, "Invoice Clerk");
        _a1 = await _w.UserAsync(_w.TenantA, "a1@t.com", null, "Invoice Approver");
        _a2 = await _w.UserAsync(_w.TenantA, "a2@t.com", null, "Invoice Approver");
        _a3 = await _w.UserAsync(_w.TenantA, "a3@t.com", null, "Invoice Approver");
        // Seeded straight into the database to prove the engine itself blocks what the role matrix normally prevents.
        _dual = await _w.UserAsync(_w.TenantA, "dual@t.com", null, "Invoice Clerk", "Invoice Approver");
        _outsider = await _w.UserAsync(_w.TenantA, "out@t.com", null, "Invoice Clerk");
        _maker = await _w.UserAsync(_w.TenantA, "vm@t.com", null);
        _checker = await _w.UserAsync(_w.TenantA, "vc@t.com", null);
        _vendor = await ActiveVendorKit.ActiveVendorAsync(_w.Host.Services, _w.TenantA, _entity, "Al Amal Trading", _maker, _checker);
        foreach (var u in new[] { _a1, _a2, _a3, _dual }) await _w.AuthorityAsync(_w.TenantA, _entity, u);
    }

    public async Task DisposeAsync() => await _w.Host.DisposeAsync();

    // --- helpers -------------------------------------------------------------------------------------------

    private async Task Publish(WorkflowDefinitionSpec spec)
    {
        var p = await _w.Workflows(_w.TenantA, _cfg, s => s.ProposeAsync(_entity, new ProposeDefinition(spec, false, "test")));
        await _w.Workflows(_w.TenantA, _chg, s => s.ApproveAsync(p.Id!.Value, null));
    }

    private async Task<InvoiceDto> VerifiedInvoice(Guid creator, string number = "INV-1", decimal net = 100m, Guid? entity = null)
    {
        var tax = Math.Round(net * 0.15m, 2);
        var data = new InvoiceData(number, TestDates.Ago(10), TestDates.Ago(10).AddDays(30), "SAR", net, tax, net + tax, null, null,
            [new InvoiceLineInput("Consulting", 1, net, net, "S", 0.15m, tax, null, null)]);
        var created = await _w.AsAsync(_w.TenantA, creator, s => s.GetRequiredService<IInvoiceService>().CreateAsync(new CreateInvoice(entity ?? _entity, _vendor, data)));
        var submitted = await _w.AsAsync(_w.TenantA, creator, s => s.GetRequiredService<IInvoiceService>().SubmitAsync(created.Id));
        Assert.Equal(InvoiceStatus.Verified, submitted.Status);
        return submitted;
    }

    private Task<InvoiceDto> Send(Guid actor, Guid invoice) => _w.AsAsync(_w.TenantA, actor, s => s.GetRequiredService<IInvoiceService>().SendForApprovalAsync(invoice));

    private Task<InvoiceDto> Invoice(Guid id) => _w.AsAsync(_w.TenantA, _w.Admin, s => s.GetRequiredService<IInvoiceService>().GetAsync(id));

    private Task<IReadOnlyList<InboxItemDto>> Inbox(Guid user, Guid? tenant = null) =>
        _w.AsAsync(tenant ?? _w.TenantA, user, s => s.GetRequiredService<IApprovalService>().InboxAsync(null));

    private Task<RunDto> Decide(Guid user, Guid task, string decision = DecisionKind.Approve, string? comment = null, Guid? actingFor = null, Guid? tenant = null) =>
        _w.AsAsync(tenant ?? _w.TenantA, user, s => s.GetRequiredService<IApprovalService>().DecideAsync(task, new DecideRequest(decision, comment, actingFor)));

    private async Task<Guid> TaskOf(Guid user) => (await Inbox(user)).Single().TaskId;

    private Task<IReadOnlyList<RunDto>> Runs(Guid invoice) => _w.AsAsync(_w.TenantA, _w.Admin, s => s.GetRequiredService<IApprovalService>().RunsForSubjectAsync("Invoice", invoice));

    private async Task<int> AuditCount(string action) => await _w.AsAsync(_w.TenantA, _w.Admin, async s =>
        await s.GetRequiredService<Vpms.Audit.Infrastructure.AuditDbContext>().Events.CountAsync(e => e.Action == action));

    // --- the happy path ------------------------------------------------------------------------------------

    [Fact]
    public async Task An_invoice_goes_through_two_levels_and_becomes_approved_for_the_version_that_was_reviewed()
    {
        await Publish(TwoLevels(_a1, _a2));
        var invoice = await VerifiedInvoice(_clerk);
        var sent = await Send(_clerk, invoice.Id);
        Assert.Equal(InvoiceStatus.InApproval, sent.Status);

        var first = (await Inbox(_a1)).Single();
        Assert.Equal("l1", first.StepId);
        Assert.Equal(invoice.Total, first.Amount);
        Assert.Contains("INV-1", first.SubjectLabel);
        Assert.Empty(await Inbox(_a2));
        Assert.Empty(await Inbox(_clerk));

        var afterFirst = await Decide(_a1, first.TaskId, comment: "Looks fine");
        Assert.Equal(RunStatus.Active, afterFirst.Status);
        Assert.Equal(InvoiceStatus.InApproval, (await Invoice(invoice.Id)).Status);
        Assert.Empty(await Inbox(_a1));

        var done = await Decide(_a2, await TaskOf(_a2));
        Assert.Equal(RunStatus.Approved, done.Status);
        var approved = await Invoice(invoice.Id);
        Assert.Equal(InvoiceStatus.Approved, approved.Status);
        Assert.Equal(1, approved.ApprovedVersion);
        Assert.NotNull(approved.ApprovedAt);

        var run = (await Runs(invoice.Id)).Single();
        Assert.Equal(2, run.Tasks.Count);
        Assert.Equal(_a1, run.Tasks[0].Decisions.Single().ActorId);
        Assert.Equal("Looks fine", run.Tasks[0].Decisions.Single().Comment);
        Assert.Equal(1, run.DefinitionVersion);

        Assert.True(await AuditCount("approval.started") >= 1);
        Assert.Equal(2, await AuditCount("approval.approve"));
        Assert.Equal(1, await AuditCount("approval.approved"));
        Assert.True(await AuditCount("invoice.sent-for-approval") >= 1);
        Assert.True(await AuditCount("invoice.approved-by-approval") >= 1);
    }

    [Fact]
    public async Task Role_approvers_are_resolved_and_a_maker_holding_the_role_is_left_out()
    {
        await Publish(new WorkflowDefinitionSpec { Name = "Role", Start = "r", Steps = [new StepSpec { Id = "r", Approvers = [Role("Invoice Approver")] }] });
        var invoice = await VerifiedInvoice(_dual);
        await Send(_dual, invoice.Id);

        Assert.NotEmpty(await Inbox(_a1));
        Assert.NotEmpty(await Inbox(_a2));
        Assert.NotEmpty(await Inbox(_a3));
        Assert.Empty(await Inbox(_dual));
    }

    // --- controls ------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_maker_cannot_approve_even_if_they_hold_the_approval_permission_and_the_attempt_is_audited()
    {
        await Publish(new WorkflowDefinitionSpec { Name = "Both", Start = "s", Steps = [new StepSpec { Id = "s", Approvers = [User(_dual), User(_a1)] }] });
        var invoice = await VerifiedInvoice(_dual);
        await Send(_dual, invoice.Id);

        var task = await TaskOf(_a1);
        Assert.Empty(await Inbox(_dual));
        var ex = await Assert.ThrowsAsync<SegregationOfDutiesException>(() => Decide(_dual, task));
        Assert.Equal("maker-checker", ex.Rule);
        Assert.Equal(1, await AuditCount("sod.invoice-approval.blocked"));
        Assert.Equal(InvoiceStatus.InApproval, (await Invoice(invoice.Id)).Status);

        // an approver the sender is not may still decide
        Assert.Equal(RunStatus.Approved, (await Decide(_a1, task)).Status);
    }

    [Fact]
    public async Task The_person_who_sends_an_invoice_for_approval_cannot_approve_it()
    {
        await Publish(new WorkflowDefinitionSpec { Name = "Sender", Start = "s", Steps = [new StepSpec { Id = "s", Approvers = [User(_dual), User(_a1)] }] });
        var invoice = await VerifiedInvoice(_clerk);
        await Send(_dual, invoice.Id); // dual only sends it; they did not prepare it

        Assert.Empty(await Inbox(_dual));
        await Assert.ThrowsAsync<SegregationOfDutiesException>(() => Decide(_dual, TaskOf(_a1).Result));
    }

    [Fact]
    public async Task An_invoice_cannot_start_when_the_only_approver_is_its_maker()
    {
        await Publish(new WorkflowDefinitionSpec { Name = "Only dual", Start = "s", Steps = [new StepSpec { Id = "s", Approvers = [User(_dual)] }] });
        var invoice = await VerifiedInvoice(_dual);
        var ex = await Assert.ThrowsAsync<ValidationFailedException>(() => Send(_dual, invoice.Id));
        Assert.Equal("no_eligible_approver", ex.Code);
        Assert.Equal(InvoiceStatus.Verified, (await Invoice(invoice.Id)).Status);
        Assert.Empty(await Runs(invoice.Id));
    }

    [Fact]
    public async Task One_person_cannot_decide_two_levels_of_the_same_invoice()
    {
        await Publish(new WorkflowDefinitionSpec
        {
            Name = "Overlap", Start = "l1",
            Steps =
            [
                new StepSpec { Id = "l1", Approvers = [User(_a1)], Next = "l2" },
                new StepSpec { Id = "l2", Approvers = [User(_a1), User(_a2)] },
            ],
        });
        var invoice = await VerifiedInvoice(_clerk);
        await Send(_clerk, invoice.Id);
        await Decide(_a1, await TaskOf(_a1));

        Assert.Empty(await Inbox(_a1));
        Assert.Single(await Inbox(_a2));
    }

    [Fact]
    public async Task Someone_who_is_not_an_approver_of_the_step_is_refused()
    {
        await Publish(TwoLevels(_a1, _a2));
        var invoice = await VerifiedInvoice(_clerk);
        await Send(_clerk, invoice.Id);
        var task = await TaskOf(_a1);

        Assert.Equal("not_an_approver", (await Assert.ThrowsAsync<ValidationFailedException>(() => Decide(_a3, task))).Code);
        Assert.Equal("not_an_approver", (await Assert.ThrowsAsync<ValidationFailedException>(() => Decide(_outsider, task))).Code);
        Assert.Equal("not_an_approver", (await Assert.ThrowsAsync<ValidationFailedException>(() => Decide(_a2, task))).Code);
    }

    [Fact]
    public async Task Approval_authority_limits_what_an_approver_can_approve_but_not_what_they_can_reject()
    {
        var limited = await _w.UserAsync(_w.TenantA, "limited@t.com", null, "Invoice Approver");
        await _w.AuthorityAsync(_w.TenantA, _entity, limited, max: 100m);
        await Publish(new WorkflowDefinitionSpec { Name = "Limited", Start = "s", Steps = [new StepSpec { Id = "s", Approvers = [User(limited)] }] });

        var big = await VerifiedInvoice(_clerk, "INV-BIG", 1_000m);
        await Send(_clerk, big.Id);
        var task = await TaskOf(limited);
        var ex = await Assert.ThrowsAsync<ValidationFailedException>(() => Decide(limited, task));
        Assert.Equal("approval_limit_exceeded", ex.Code);
        Assert.Equal(RunStatus.Active, (await Runs(big.Id)).Single().Status);

        var rejected = await Decide(limited, task, DecisionKind.Reject, "above my limit");
        Assert.Equal(RunStatus.Rejected, rejected.Status);
        Assert.Equal(InvoiceStatus.Rejected, (await Invoice(big.Id)).Status);
    }

    [Fact]
    public async Task An_approver_without_any_authority_row_cannot_approve()
    {
        var noAuthority = await _w.UserAsync(_w.TenantA, "noauth@t.com", null, "Invoice Approver");
        await Publish(new WorkflowDefinitionSpec { Name = "NoAuth", Start = "s", Steps = [new StepSpec { Id = "s", Approvers = [User(noAuthority)] }] });
        var invoice = await VerifiedInvoice(_clerk);
        await Send(_clerk, invoice.Id);
        Assert.Equal("approval_limit_exceeded", (await Assert.ThrowsAsync<ValidationFailedException>(() => Decide(noAuthority, TaskOf(noAuthority).Result))).Code);
    }

    [Fact]
    public async Task Losing_the_approval_permission_after_the_task_was_created_stops_the_decision()
    {
        await Publish(TwoLevels(_a1, _a2));
        var invoice = await VerifiedInvoice(_clerk);
        await Send(_clerk, invoice.Id);
        var task = await TaskOf(_a1);

        await _w.AsAsync(_w.TenantA, _w.Admin, async s =>
        {
            var db = s.GetRequiredService<Vpms.Identity.Infrastructure.IdentityDbContext>();
            await db.RoleAssignments.Where(a => a.UserId == _a1).ExecuteDeleteAsync();
            return 0;
        });

        Assert.Equal("not_an_approver", (await Assert.ThrowsAsync<ValidationFailedException>(() => Decide(_a1, task))).Code);
    }

    // --- outcomes other than approval ---------------------------------------------------------------------

    [Fact]
    public async Task Rejection_ends_the_approval_and_rejects_the_invoice_with_the_reason()
    {
        await Publish(TwoLevels(_a1, _a2));
        var invoice = await VerifiedInvoice(_clerk);
        await Send(_clerk, invoice.Id);

        Assert.Equal("comment_required", (await Assert.ThrowsAsync<ValidationFailedException>(() => Decide(_a1, TaskOf(_a1).Result, DecisionKind.Reject))).Code);
        var run = await Decide(_a1, await TaskOf(_a1), DecisionKind.Reject, "Not our vendor");
        Assert.Equal(RunStatus.Rejected, run.Status);

        var after = await Invoice(invoice.Id);
        Assert.Equal(InvoiceStatus.Rejected, after.Status);
        Assert.Equal("Not our vendor", after.StatusReason);
        Assert.Empty(await Inbox(_a2));
    }

    [Fact]
    public async Task A_returned_invoice_can_be_corrected_and_sent_again_which_starts_a_new_run_and_keeps_the_old_one()
    {
        await Publish(TwoLevels(_a1, _a2));
        var invoice = await VerifiedInvoice(_clerk);
        await Send(_clerk, invoice.Id);
        await Decide(_a1, await TaskOf(_a1), DecisionKind.Return, "Please attach the delivery note");

        var returned = await Invoice(invoice.Id);
        Assert.Equal(InvoiceStatus.Returned, returned.Status);
        Assert.Equal("Please attach the delivery note", returned.StatusReason);

        // correct it: any edit gives a new version that is verified again
        var data = new InvoiceData(returned.InvoiceNumber, returned.InvoiceDate, returned.DueDate, "SAR", returned.Subtotal, returned.TaxTotal, returned.Total, null, "ref-2",
            returned.Lines.Select(l => new InvoiceLineInput(l.Description, l.Quantity, l.UnitPrice, l.LineAmount, l.TaxCode, l.TaxRate, l.TaxAmount, l.PoLineRef, l.DimensionsJson)).ToList());
        var edited = await _w.AsAsync(_w.TenantA, _clerk, s => s.GetRequiredService<IInvoiceService>().UpdateAsync(invoice.Id, data));
        Assert.Equal(InvoiceStatus.Verified, edited.Status);
        Assert.Equal(2, edited.Version);

        await Send(_clerk, invoice.Id);
        var runs = await Runs(invoice.Id);
        Assert.Equal([RunStatus.Active, RunStatus.Returned], runs.Select(r => r.Status));
        Assert.Equal(2, runs[0].SubjectVersion);

        await Decide(_a1, await TaskOf(_a1));
        await Decide(_a2, await TaskOf(_a2));
        var approved = await Invoice(invoice.Id);
        Assert.Equal(InvoiceStatus.Approved, approved.Status);
        Assert.Equal(2, approved.ApprovedVersion);
    }

    [Fact]
    public async Task An_invoice_in_approval_cannot_be_edited_or_sent_twice()
    {
        await Publish(TwoLevels(_a1, _a2));
        var invoice = await VerifiedInvoice(_clerk);
        await Send(_clerk, invoice.Id);

        Assert.Equal("invalid_transition", (await Assert.ThrowsAsync<ConflictException>(() => Send(_clerk, invoice.Id))).Code);
        var data = new InvoiceData("X", invoice.InvoiceDate, null, "SAR", 1, 0, 1, null, null, []);
        await Assert.ThrowsAsync<ConflictException>(() => _w.AsAsync(_w.TenantA, _clerk, s => s.GetRequiredService<IInvoiceService>().UpdateAsync(invoice.Id, data)));
    }

    [Fact]
    public async Task Without_an_approved_workflow_the_invoice_stays_verified()
    {
        var invoice = await VerifiedInvoice(_clerk);
        var ex = await Assert.ThrowsAsync<ConflictException>(() => Send(_clerk, invoice.Id));
        Assert.Equal("no_workflow", ex.Code);
        Assert.Equal(InvoiceStatus.Verified, (await Invoice(invoice.Id)).Status);

        // a pending (not yet approved) proposal does not count either
        await _w.Workflows(_w.TenantA, _cfg, s => s.ProposeAsync(_entity, new ProposeDefinition(TwoLevels(_a1, _a2), false, "proposal")));
        Assert.Equal("no_workflow", (await Assert.ThrowsAsync<ConflictException>(() => Send(_clerk, invoice.Id))).Code);
    }

    [Fact]
    public async Task A_workflow_that_needs_no_decision_for_this_invoice_approves_it_immediately()
    {
        await Publish(new WorkflowDefinitionSpec
        {
            Name = "Small is free", Start = "gate",
            Steps =
            [
                new StepSpec { Id = "gate", Type = StepType.Condition, Default = "review", Cases = [new CaseSpec([new ClauseSpec("amount", ConditionOp.Le, ["200"])], Targets.End)] },
                new StepSpec { Id = "review", Approvers = [User(_a1)] },
            ],
        });

        var small = await Send(_clerk, (await VerifiedInvoice(_clerk, "SMALL", 100m)).Id);
        Assert.Equal(InvoiceStatus.Approved, small.Status);
        Assert.Equal(1, small.ApprovedVersion);

        var big = await Send(_clerk, (await VerifiedInvoice(_clerk, "BIG", 5_000m)).Id);
        Assert.Equal(InvoiceStatus.InApproval, big.Status);
    }

    // --- versioning and robustness -------------------------------------------------------------------------

    [Fact]
    public async Task A_run_that_started_on_one_version_finishes_on_it_while_new_invoices_use_the_new_version()
    {
        await Publish(TwoLevels(_a1, _a2));
        var old = await VerifiedInvoice(_clerk, "OLD");
        await Send(_clerk, old.Id);

        await Publish(new WorkflowDefinitionSpec { Name = "v2", Start = "only", Steps = [new StepSpec { Id = "only", Approvers = [User(_a3)] }] });
        var fresh = await VerifiedInvoice(_clerk, "NEW");
        await Send(_clerk, fresh.Id);

        Assert.Equal(1, (await Runs(old.Id)).Single().DefinitionVersion);
        Assert.Equal(2, (await Runs(fresh.Id)).Single().DefinitionVersion);

        // a3 only sees the new invoice; the old run still follows its two levels
        Assert.Contains("NEW", (await Inbox(_a3)).Single().SubjectLabel);
        Assert.Contains("OLD", (await Inbox(_a1)).Single().SubjectLabel);
        await Decide(_a1, await TaskOf(_a1));
        await Decide(_a2, await TaskOf(_a2));
        Assert.Equal(InvoiceStatus.Approved, (await Invoice(old.Id)).Status);
    }

    [Fact]
    public async Task Starting_the_same_approval_twice_returns_the_same_run()
    {
        await Publish(TwoLevels(_a1, _a2));
        var invoice = await VerifiedInvoice(_clerk);
        var subject = new ApprovalSubject("Invoice", invoice.Id, invoice.Version, _entity, "INV-1", invoice.Total, "SAR", _vendor, "Manual", new Dictionary<string, string>(), [_clerk]);

        var first = await _w.AsAsync(_w.TenantA, _clerk, s => s.GetRequiredService<IApprovalStarter>().StartAsync(subject));
        var second = await _w.AsAsync(_w.TenantA, _clerk, s => s.GetRequiredService<IApprovalStarter>().StartAsync(subject));
        Assert.Equal(first.RunId, second.RunId);
        Assert.Single(await Runs(invoice.Id));

        var newer = subject with { SubjectVersion = invoice.Version + 1 };
        Assert.Equal("approval_in_progress", (await Assert.ThrowsAsync<ConflictException>(() => _w.AsAsync(_w.TenantA, _clerk, s => s.GetRequiredService<IApprovalStarter>().StartAsync(newer)))).Code);
    }

    [Fact]
    public async Task Two_people_deciding_at_the_same_moment_record_exactly_one_decision()
    {
        await Publish(new WorkflowDefinitionSpec { Name = "Race", Start = "s", Steps = [new StepSpec { Id = "s", Approvers = [User(_a1), User(_a2)] }] });
        var invoice = await VerifiedInvoice(_clerk);
        await Send(_clerk, invoice.Id);
        var task = await TaskOf(_a1);

        var results = await Task.WhenAll(Attempt(_a1, task), Attempt(_a2, task));
        Assert.Equal(1, results.Count(ok => ok));
        var run = (await Runs(invoice.Id)).Single();
        Assert.Equal(RunStatus.Approved, run.Status);
        Assert.Single(run.Tasks[0].Decisions);
        Assert.Equal(InvoiceStatus.Approved, (await Invoice(invoice.Id)).Status);
    }

    private async Task<bool> Attempt(Guid user, Guid task)
    {
        try
        {
            await Decide(user, task);
            return true;
        }
        catch (Exception ex) when (ex is ConflictException or DbUpdateException)
        {
            return false;
        }
    }

    [Fact]
    public async Task A_failed_outcome_is_retried_by_the_sweep_and_the_decision_is_not_lost()
    {
        await Publish(TwoLevels(_a1, _a2));
        var invoice = await VerifiedInvoice(_clerk);
        await Send(_clerk, invoice.Id);
        await Decide(_a1, await TaskOf(_a1));

        _flaky.Failing = true;
        var run = await Decide(_a2, await TaskOf(_a2));
        Assert.Equal(RunStatus.Approved, run.Status);
        Assert.Equal(InvoiceStatus.InApproval, (await Invoice(invoice.Id)).Status);
        Assert.Contains(_w.TenantA, await _w.AsAsync(_w.TenantA, _w.Admin, s => s.GetRequiredService<IApprovalScan>().TenantsWithPendingOutcomesAsync()));

        _flaky.Failing = false;
        var applied = await _w.AsAsync(_w.TenantA, _w.Admin, s => s.GetRequiredService<IApprovalService>().ApplyPendingOutcomesAsync());
        Assert.Equal(1, applied);
        Assert.Equal(InvoiceStatus.Approved, (await Invoice(invoice.Id)).Status);
        Assert.Equal(0, await _w.AsAsync(_w.TenantA, _w.Admin, s => s.GetRequiredService<IApprovalService>().ApplyPendingOutcomesAsync()));
    }

    [Fact]
    public async Task Applying_the_same_outcome_twice_changes_nothing()
    {
        await Publish(new WorkflowDefinitionSpec { Name = "One", Start = "s", Steps = [new StepSpec { Id = "s", Approvers = [User(_a1)] }] });
        var invoice = await VerifiedInvoice(_clerk);
        await Send(_clerk, invoice.Id);
        var run = await Decide(_a1, await TaskOf(_a1));
        var approvedAt = (await Invoice(invoice.Id)).ApprovedAt;

        var outcome = new ApprovalOutcome("Invoice", invoice.Id, 1, run.Id, ApprovalOutcomes.Approved, null, DateTimeOffset.UtcNow.AddHours(1));
        await _w.AsAsync(_w.TenantA, _w.Admin, async s =>
        {
            await ActivatorUtilities.CreateInstance<InvoiceApprovalOutcomeHandler>(s).ApplyAsync(outcome);
            return 0;
        });
        Assert.Equal(approvedAt, (await Invoice(invoice.Id)).ApprovedAt);
    }

    // --- isolation -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Another_tenant_cannot_see_or_decide_approvals()
    {
        await Publish(TwoLevels(_a1, _a2));
        var invoice = await VerifiedInvoice(_clerk);
        await Send(_clerk, invoice.Id);
        var task = await TaskOf(_a1);

        Assert.Empty(await Inbox(_a1, _w.TenantB));
        await Assert.ThrowsAsync<NotFoundException>(() => Decide(_a1, task, tenant: _w.TenantB));
        Assert.Empty(await _w.AsAsync(_w.TenantB, _a1, s => s.GetRequiredService<IApprovalService>().RunsForSubjectAsync("Invoice", invoice.Id)));
        await Assert.ThrowsAsync<NotFoundException>(() => _w.AsAsync(_w.TenantB, _a1, s => s.GetRequiredService<IApprovalService>().GetRunOfTaskAsync(task)));
    }

    [Fact]
    public async Task Approvals_of_one_legal_entity_do_not_use_the_workflow_of_another()
    {
        await Publish(TwoLevels(_a1, _a2));
        await ActiveVendorKit.LinkAsync(_w.Host.Services, _w.TenantA, _maker, _vendor, _otherEntity);
        var invoice = await VerifiedInvoice(_clerk, "OTHER", entity: _otherEntity);
        var ex = await Assert.ThrowsAsync<ConflictException>(() => Send(_clerk, invoice.Id));
        Assert.Equal("no_workflow", ex.Code);
    }

    // --- test doubles --------------------------------------------------------------------------------------

    private sealed class FlakyOutcome
    {
        public volatile bool Failing;
    }

    private sealed class FlakyHandler(IApprovalOutcomeHandler inner, FlakyOutcome flaky) : IApprovalOutcomeHandler
    {
        public string SubjectType => inner.SubjectType;

        public Task ApplyAsync(ApprovalOutcome outcome, CancellationToken ct = default) =>
            flaky.Failing ? throw new InvalidOperationException("simulated outage") : inner.ApplyAsync(outcome, ct);
    }
}
