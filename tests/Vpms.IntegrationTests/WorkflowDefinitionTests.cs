using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vpms.BuildingBlocks.Errors;
using Vpms.BuildingBlocks.Security;
using Vpms.CustomerAdmin.Contracts;
using Vpms.TestSupport;
using Vpms.Workflow.Application;
using Vpms.Workflow.Domain;
using Vpms.Workflow.Infrastructure;
using static Vpms.IntegrationTests.WorkflowWorld;

namespace Vpms.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class WorkflowDefinitionTests : IAsyncLifetime
{
    private readonly SqlServerFixture _sql;
    private WorkflowWorld _w = null!;
    private Guid _entityA, _entityB, _entityOfB;
    private Guid _configurer, _changeApprover, _approver1, _approver2, _clerk;

    public WorkflowDefinitionTests(SqlServerFixture sql) => _sql = sql;

    public async Task InitializeAsync()
    {
        _w = new WorkflowWorld(new IntegrationHost(await _sql.CreateDatabaseAsync()));
        await _w.InitializeAsync();
        _entityA = await _w.EntityAsync(_w.TenantA, "A1");
        _entityB = await _w.EntityAsync(_w.TenantA, "A2");
        _entityOfB = await _w.EntityAsync(_w.TenantB, "B1");
        _configurer = await _w.UserAsync(_w.TenantA, "cfg@t.com", null, "Workflow Administrator");
        _changeApprover = await _w.UserAsync(_w.TenantA, "chg@t.com", null, "Workflow Change Approver");
        _approver1 = await _w.UserAsync(_w.TenantA, "ap1@t.com", null, "Invoice Approver");
        _approver2 = await _w.UserAsync(_w.TenantA, "ap2@t.com", null, "Invoice Approver");
        _clerk = await _w.UserAsync(_w.TenantA, "clerk@t.com", null, "Invoice Clerk");
    }

    public async Task DisposeAsync() => await _w.Host.DisposeAsync();

    private Task<DefinitionDto> ProposeSpec(WorkflowDefinitionSpec? spec = null, Guid? entity = null, string note = "Initial routing") =>
        _w.Workflows(_w.TenantA, _configurer, s => s.ProposeAsync(entity ?? _entityA, new ProposeDefinition(spec ?? TwoLevels(_approver1, _approver2), false, note)));

    private async Task<DefinitionDto> ProposeAndApprove(WorkflowDefinitionSpec? spec = null, Guid? entity = null)
    {
        var p = await ProposeSpec(spec, entity);
        return await _w.Workflows(_w.TenantA, _changeApprover, s => s.ApproveAsync(p.Id!.Value, "ok"));
    }

    [Fact]
    public async Task An_entity_has_no_workflow_until_one_is_approved()
    {
        var none = await _w.Workflows(_w.TenantA, _configurer, s => s.GetAsync(_entityA));
        Assert.Null(none.Active);
        Assert.Null(none.Pending);

        var proposed = await ProposeSpec();
        Assert.Equal(DefinitionStatus.Pending, proposed.Status);
        Assert.Equal(1, proposed.VersionNo);
        Assert.Null((await _w.Workflows(_w.TenantA, _configurer, s => s.GetAsync(_entityA))).Active);
        Assert.Null(await _w.Workflows(_w.TenantA, _configurer, s => s.GetActiveAsync(_entityA, SubjectTypes.Invoice)));

        var active = await _w.Workflows(_w.TenantA, _changeApprover, s => s.ApproveAsync(proposed.Id!.Value, "looks right"));
        Assert.Equal(DefinitionStatus.Active, active.Status);
        Assert.Equal(_changeApprover, active.DecidedBy);
        var running = await _w.Workflows(_w.TenantA, _configurer, s => s.GetActiveAsync(_entityA, SubjectTypes.Invoice));
        Assert.Equal(1, running!.VersionNo);
        Assert.Equal(2, running.Spec.Steps.Count);
    }

    [Fact]
    public async Task The_person_who_proposed_a_change_cannot_approve_or_reject_it_and_the_attempt_is_audited()
    {
        var p = await ProposeSpec();
        var ex = await Assert.ThrowsAsync<SegregationOfDutiesException>(() => _w.Workflows(_w.TenantA, _configurer, s => s.ApproveAsync(p.Id!.Value, null)));
        Assert.Equal("maker-checker", ex.Rule);
        await Assert.ThrowsAsync<SegregationOfDutiesException>(() => _w.Workflows(_w.TenantA, _configurer, s => s.RejectAsync(p.Id!.Value, "no")));

        var events = await _w.AsAsync(_w.TenantA, _w.Admin, async s =>
            await s.GetRequiredService<Vpms.Audit.Infrastructure.AuditDbContext>().Events.CountAsync(e => e.Action == "sod.workflow-change.blocked" && e.EntityId == p.Id.ToString()));
        Assert.Equal(2, events);
        Assert.Equal(DefinitionStatus.Pending, (await _w.Workflows(_w.TenantA, _configurer, s => s.GetAsync(_entityA))).Pending!.Status);
    }

    [Fact]
    public async Task A_new_version_replaces_the_active_one_which_stays_in_the_history()
    {
        var v1 = await ProposeAndApprove();
        var v2 = await ProposeAndApprove(new WorkflowDefinitionSpec
        {
            Name = "One level", Start = "only",
            Steps = [new StepSpec { Id = "only", Approvers = [User(_approver1)] }],
        });

        Assert.Equal(2, v2.VersionNo);
        var history = await _w.Workflows(_w.TenantA, _configurer, s => s.HistoryAsync(_entityA));
        Assert.Equal([DefinitionStatus.Active, DefinitionStatus.Superseded], history.Select(h => h.Status));
        Assert.Equal(v1.Id, history[1].Id);
        Assert.Equal(2, (await _w.Workflows(_w.TenantA, _configurer, s => s.GetActiveAsync(_entityA, SubjectTypes.Invoice)))!.VersionNo);
        Assert.Equal(2, (await _w.Workflows(_w.TenantA, _configurer, s => s.GetVersionAsync(v1.Id!.Value)))!.Spec.Steps.Count);
    }

    [Fact]
    public async Task Only_one_change_can_wait_and_unchanged_workflows_are_refused()
    {
        await ProposeSpec();
        Assert.Equal("change_pending", (await Assert.ThrowsAsync<ConflictException>(() => ProposeSpec())).Code);

        var changes = await _w.Workflows(_w.TenantA, _changeApprover, s => s.ListChangesAsync("Pending", null));
        await _w.Workflows(_w.TenantA, _changeApprover, s => s.ApproveAsync(changes.Single().Change.Id!.Value, null));
        Assert.Equal("no_change", (await Assert.ThrowsAsync<ConflictException>(() => ProposeSpec())).Code);
    }

    [Fact]
    public async Task Rejecting_needs_a_reason_and_cancelling_is_only_for_the_maker()
    {
        var p = await ProposeSpec();
        Assert.Equal("reason_required", (await Assert.ThrowsAsync<ValidationFailedException>(() => _w.Workflows(_w.TenantA, _changeApprover, s => s.RejectAsync(p.Id!.Value, " ")))).Code);
        Assert.Equal("not_the_maker", (await Assert.ThrowsAsync<ConflictException>(() => _w.Workflows(_w.TenantA, _changeApprover, s => s.CancelAsync(p.Id!.Value)))).Code);

        var cancelled = await _w.Workflows(_w.TenantA, _configurer, s => s.CancelAsync(p.Id!.Value));
        Assert.Equal(DefinitionStatus.Cancelled, cancelled.Status);
        Assert.Equal("not_pending", (await Assert.ThrowsAsync<ConflictException>(() => _w.Workflows(_w.TenantA, _changeApprover, s => s.ApproveAsync(p.Id!.Value, null)))).Code);

        var again = await ProposeSpec();
        var rejected = await _w.Workflows(_w.TenantA, _changeApprover, s => s.RejectAsync(again.Id!.Value, "wrong approver"));
        Assert.Equal(DefinitionStatus.Rejected, rejected.Status);
        Assert.Equal([DefinitionStatus.Rejected, DefinitionStatus.Cancelled], (await _w.Workflows(_w.TenantA, _configurer, s => s.HistoryAsync(_entityA))).Select(h => h.Status));
    }

    [Fact]
    public async Task Named_approvers_must_hold_the_approval_permission()
    {
        var bad = TwoLevels(_clerk, _approver2);
        Assert.Equal("approver_not_permitted", (await Assert.ThrowsAsync<ValidationFailedException>(() => ProposeSpec(bad))).Code);

        var unknown = TwoLevels(Guid.NewGuid(), _approver2);
        Assert.Equal("approver_not_permitted", (await Assert.ThrowsAsync<ValidationFailedException>(() => ProposeSpec(unknown))).Code);

        // an approver who only holds the role for another legal entity does not count
        var elsewhere = await _w.UserAsync(_w.TenantA, "else@t.com", _entityB, "Invoice Approver");
        Assert.Equal("approver_not_permitted", (await Assert.ThrowsAsync<ValidationFailedException>(() => ProposeSpec(TwoLevels(elsewhere, _approver2)))).Code);
        await ProposeSpec(TwoLevels(elsewhere, _approver2), _entityB);
    }

    [Fact]
    public async Task Invalid_definitions_are_refused_with_the_first_problem()
    {
        var cyclic = new WorkflowDefinitionSpec
        {
            Name = "Loop", Start = "a",
            Steps = [new StepSpec { Id = "a", Approvers = [User(_approver1)], Next = "b" }, new StepSpec { Id = "b", Approvers = [User(_approver2)], Next = "a" }],
        };
        Assert.Equal("invalid_definition", (await Assert.ThrowsAsync<ValidationFailedException>(() => ProposeSpec(cyclic))).Code);
        Assert.Equal("reason_required", (await Assert.ThrowsAsync<ValidationFailedException>(() => ProposeSpec(note: " "))).Code);

        var issues = await _w.Workflows(_w.TenantA, _configurer, s => Task.FromResult(s.Validate(cyclic)));
        Assert.Contains(issues, i => i.Code == "cycle");
    }

    [Fact]
    public async Task Entities_and_tenants_have_independent_workflows()
    {
        await ProposeAndApprove(entity: _entityA);
        Assert.Null((await _w.Workflows(_w.TenantA, _configurer, s => s.GetAsync(_entityB))).Active);

        // another tenant cannot see tenant A's entity at all
        await Assert.ThrowsAsync<NotFoundException>(() => _w.Workflows(_w.TenantB, _configurer, s => s.GetAsync(_entityA)));
        Assert.Empty(await _w.Workflows(_w.TenantB, _configurer, s => s.ListChangesAsync(null, null)));
        Assert.Null(await _w.Workflows(_w.TenantB, _configurer, s => s.GetActiveAsync(_entityA, SubjectTypes.Invoice)));
        await Assert.ThrowsAsync<NotFoundException>(() => _w.Workflows(_w.TenantB, _configurer, s => s.ApproveAsync(Guid.NewGuid(), null)));
    }

    [Fact]
    public async Task The_changes_list_can_be_limited_to_the_entities_in_scope()
    {
        await ProposeSpec(entity: _entityA);
        await ProposeSpec(TwoLevels(_approver1, _approver2), _entityB);

        Assert.Equal(2, (await _w.Workflows(_w.TenantA, _changeApprover, s => s.ListChangesAsync("Pending", null))).Count);
        var scoped = await _w.Workflows(_w.TenantA, _changeApprover, s => s.ListChangesAsync("Pending", new HashSet<Guid> { _entityB }));
        Assert.Equal(_entityB, scoped.Single().Change.LegalEntityId);
    }

    // --- approval matrix -----------------------------------------------------------------------------------

    [Fact]
    public async Task The_approval_matrix_compiles_to_a_workflow_that_routes_by_priority_and_conditions()
    {
        var financeManager = await _w.UserAsync(_w.TenantA, "fm@t.com", _entityA, "Invoice Approver");
        await _w.MatrixRuleAsync(_w.TenantA, _entityA, "Small", 10, "{\"amountTo\":10000}", Levels(Level("Invoice Approver")), 1);
        await _w.MatrixRuleAsync(_w.TenantA, _entityA, "Large", 20, "{\"amountFrom\":10000.01}", Levels(Level("Invoice Approver"), Level("Invoice Approver")), 2);

        var proposed = await _w.Workflows(_w.TenantA, _configurer, s => s.ProposeAsync(_entityA, new ProposeDefinition(null, true, "From matrix")));
        Assert.Equal(DefinitionSource.Matrix, proposed.Source);
        await _w.Workflows(_w.TenantA, _changeApprover, s => s.ApproveAsync(proposed.Id!.Value, null));

        var small = await _w.Workflows(_w.TenantA, _configurer, s => s.SimulateAsync(_entityA, new SimulationRequest(null, 500, "SAR", null, null, null, null)));
        Assert.Equal(RunStatus.Approved, small.Outcome);
        Assert.Equal(["m1-l1"], small.Route.Select(r => r.StepId));

        var large = await _w.Workflows(_w.TenantA, _configurer, s => s.SimulateAsync(_entityA, new SimulationRequest(null, 50_000, "SAR", null, null, null, _clerk)));
        Assert.Equal(["m2-l1", "m2-l2"], large.Route.Select(r => r.StepId));
        Assert.Contains(financeManager, large.Route[0].Approvers);
    }

    [Fact]
    public async Task Matrix_conditions_cover_currency_vendor_and_dimensions()
    {
        var vendor = Guid.NewGuid();
        await _w.MatrixRuleAsync(_w.TenantA, _entityA, "Strategic vendor", 1, $"{{\"vendors\":[\"{vendor}\"]}}", Levels(Level("Invoice Approver")), 1);
        await _w.MatrixRuleAsync(_w.TenantA, _entityA, "IT spend", 2, "{\"dimensions\":{\"CostCenter\":[\"IT\",\"SEC\"]},\"currency\":\"SAR\"}", Levels(Level("Invoice Approver")), 1);
        var p = await _w.Workflows(_w.TenantA, _configurer, s => s.ProposeAsync(_entityA, new ProposeDefinition(null, true, "matrix")));
        await _w.Workflows(_w.TenantA, _changeApprover, s => s.ApproveAsync(p.Id!.Value, null));

        Task<SimulationDto> Sim(Guid? v, string currency, string? cc) => _w.Workflows(_w.TenantA, _configurer, s => s.SimulateAsync(_entityA,
            new SimulationRequest(null, 100, currency, v, null, cc is null ? null : new Dictionary<string, string> { ["CostCenter"] = cc }, null)));

        Assert.Equal(["m1-l1"], (await Sim(vendor, "SAR", null)).Route.Select(r => r.StepId));
        Assert.Equal(["m2-l1"], (await Sim(null, "SAR", "it")).Route.Select(r => r.StepId));
        var none = await Sim(null, "USD", "IT");
        Assert.Equal("NoRoute", none.Outcome);
        Assert.Empty(none.Route);
        Assert.Equal("NoRoute", (await Sim(null, "SAR", "HR")).Outcome);
    }

    [Fact]
    public async Task A_matrix_that_is_missing_or_malformed_cannot_be_proposed()
    {
        Assert.Equal("matrix_empty", (await Assert.ThrowsAsync<ValidationFailedException>(() =>
            _w.Workflows(_w.TenantA, _configurer, s => s.ProposeAsync(_entityA, new ProposeDefinition(null, true, "x"))))).Code);

        await _w.MatrixRuleAsync(_w.TenantA, _entityB, "Wrong levels", 1, "{}", Levels(Level("Invoice Approver")), 2);
        Assert.Equal("matrix_levels", (await Assert.ThrowsAsync<ValidationFailedException>(() => _w.Workflows(_w.TenantA, _configurer, s => s.PreviewMatrixAsync(_entityB)))).Code);
    }

    [Fact]
    public async Task Disabled_matrix_rules_are_left_out_and_rules_of_other_entities_do_not_leak()
    {
        await _w.MatrixRuleAsync(_w.TenantA, _entityA, "A rule", 1, "{}", Levels(Level("Invoice Approver")), 1);
        await _w.MatrixRuleAsync(_w.TenantA, _entityB, "B rule", 1, "{}", Levels(Level("Invoice Approver")), 1);
        var lookup = await _w.AsAsync(_w.TenantA, _w.Admin, s => s.GetRequiredService<IApprovalDataLookup>().GetMatrixRulesAsync(_entityA));
        Assert.Equal(["A rule"], lookup.Select(r => r.Name));

        var preview = await _w.Workflows(_w.TenantA, _configurer, s => s.PreviewMatrixAsync(_entityA));
        Assert.Equal(2, preview.Steps.Count);
        Assert.Empty(await _w.AsAsync(_w.TenantB, _w.Admin, s => s.GetRequiredService<IApprovalDataLookup>().GetMatrixRulesAsync(_entityA)));
    }

    // --- simulation ----------------------------------------------------------------------------------------

    [Fact]
    public async Task Simulation_reports_missing_workflows_unreachable_approvers_and_invalid_drafts()
    {
        var none = await _w.Workflows(_w.TenantA, _configurer, s => s.SimulateAsync(_entityA, new SimulationRequest(null, 10, "SAR", null, null, null, null)));
        Assert.Equal("NoWorkflow", none.Outcome);

        var roleNobodyHolds = new WorkflowDefinitionSpec { Name = "x", Start = "a", Steps = [new StepSpec { Id = "a", Approvers = [Role("Nobody")] }] };
        var stalled = await _w.Workflows(_w.TenantA, _configurer, s => s.SimulateAsync(_entityA, new SimulationRequest(roleNobodyHolds, 10, "SAR", null, null, null, null)));
        Assert.Equal("NoApprover", stalled.Outcome);

        var invalid = new WorkflowDefinitionSpec { Name = "x", Start = "zzz", Steps = [new StepSpec { Id = "a", Approvers = [Role("Nobody")] }] };
        var bad = await _w.Workflows(_w.TenantA, _configurer, s => s.SimulateAsync(_entityA, new SimulationRequest(invalid, 10, "SAR", null, null, null, null)));
        Assert.Equal("Invalid", bad.Outcome);
        Assert.Contains(bad.Issues, i => i.Code == "start_missing");
    }

    [Fact]
    public async Task Simulation_excludes_the_maker_from_the_route()
    {
        var spec = new WorkflowDefinitionSpec { Name = "x", Start = "a", Steps = [new StepSpec { Id = "a", Approvers = [User(_approver1), User(_approver2)] }] };
        var r = await _w.Workflows(_w.TenantA, _configurer, s => s.SimulateAsync(_entityA, new SimulationRequest(spec, 10, "SAR", null, null, null, _approver1)));
        Assert.Equal([_approver2], r.Route.Single().Approvers);
    }

    [Fact]
    public async Task Stored_definitions_cannot_be_deleted_or_rewritten_by_the_application_login()
    {
        var active = await ProposeAndApprove();
        await Assert.ThrowsAnyAsync<Exception>(() => _w.AsAsync(_w.TenantA, _w.Admin, async s =>
        {
            var db = s.GetRequiredService<WorkflowDbContext>();
            await db.Definitions.Where(d => d.Id == active.Id).ExecuteDeleteAsync();
            return 0;
        }));
    }

    // --- approval authority --------------------------------------------------------------------------------

    [Fact]
    public async Task Authority_covers_amount_currency_dates_and_dimension_scope()
    {
        var now = DateTimeOffset.UtcNow;
        var user = await _w.UserAsync(_w.TenantA, "auth@t.com", null, "Invoice Approver");
        await _w.AuthorityAsync(_w.TenantA, _entityA, user, 5_000, "SAR", "{\"CostCenter\":[\"IT\"]}", now.AddDays(-1), now.AddDays(10));

        Task<bool> Can(decimal amount, string currency = "SAR", string? cc = "IT", Guid? entity = null, DateTimeOffset? at = null) =>
            _w.AsAsync(_w.TenantA, _w.Admin, s => s.GetRequiredService<IApprovalDataLookup>().HasAuthorityAsync(entity ?? _entityA, user, amount, currency,
                cc is null ? new Dictionary<string, string>() : new Dictionary<string, string> { ["CostCenter"] = cc }, at ?? now));

        Assert.True(await Can(5_000));
        Assert.False(await Can(5_000.01m));
        Assert.False(await Can(100, "USD"));
        Assert.False(await Can(100, cc: "HR"));
        Assert.False(await Can(100, cc: null));
        Assert.False(await Can(100, entity: _entityB));
        Assert.False(await Can(100, at: now.AddDays(11)));
        Assert.False(await Can(100, at: now.AddDays(-2)));
    }

    [Fact]
    public async Task Without_an_authority_row_nobody_may_approve_and_revoked_rows_do_not_count()
    {
        var user = await _w.UserAsync(_w.TenantA, "noauth@t.com", null, "Invoice Approver");
        Task<bool> Can() => _w.AsAsync(_w.TenantA, _w.Admin, s => s.GetRequiredService<IApprovalDataLookup>()
            .HasAuthorityAsync(_entityA, user, 1, "SAR", new Dictionary<string, string>(), DateTimeOffset.UtcNow));
        Assert.False(await Can());

        await _w.AuthorityAsync(_w.TenantA, _entityA, user);
        Assert.True(await Can());

        var id = (await _w.AsAsync(_w.TenantA, _w.Admin, s => s.GetRequiredService<Vpms.CustomerAdmin.Application.IApprovalAdminService>().ListAuthoritiesAsync(_entityA, null))).Single().Id;
        await _w.AsAsync(_w.TenantA, _w.Admin, async s =>
        {
            await s.GetRequiredService<Vpms.CustomerAdmin.Application.IApprovalAdminService>().RevokeAuthorityAsync(id);
            return 0;
        });
        Assert.False(await Can());
    }
}
