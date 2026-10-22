using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vpms.BuildingBlocks.Errors;
using Vpms.BuildingBlocks.Security;
using Vpms.BuildingBlocks.Tenancy;
using Vpms.CustomerAdmin.Domain;
using Vpms.CustomerAdmin.Infrastructure;
using Vpms.Invoices.Application;
using Vpms.Invoices.Domain.Verification;
using Vpms.Invoices.Infrastructure;
using Vpms.TestSupport;

namespace Vpms.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class VerificationRuleSetTests : IAsyncLifetime
{
    private readonly SqlServerFixture _sql;
    private IntegrationHost _host = null!;
    private readonly Guid _tenantA = Guid.NewGuid();
    private readonly Guid _tenantB = Guid.NewGuid();
    private readonly Guid _admin = Guid.NewGuid();
    private readonly Guid _approver = Guid.NewGuid();
    private Guid _entityA, _entityB;

    public VerificationRuleSetTests(SqlServerFixture sql) => _sql = sql;

    public async Task InitializeAsync()
    {
        _host = new IntegrationHost(await _sql.CreateDatabaseAsync());
        foreach (var t in new[] { _tenantA, _tenantB })
        {
            await using var scope = _host.Scope(null);
            using var _ = scope.ServiceProvider.GetRequiredService<TenantContext>().BeginPlatformScope();
            var admin = scope.ServiceProvider.GetRequiredService<CustomerAdminDbContext>();
            admin.Tenants.Add(new Tenant { Id = t, Name = "T", Slug = "t" + t.ToString("N")[..6], CreatedAt = DateTimeOffset.UtcNow });
            await admin.SaveChangesAsync();
        }

        _entityA = await ActiveVendorKit.CreateEntityAsync(_host.Services, _tenantA, _admin, "KSA");
        _entityB = await ActiveVendorKit.CreateEntityAsync(_host.Services, _tenantA, _admin, "UAE", "AE", "AED");
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private Task<T> As<T>(Guid actor, Func<IVerificationRuleSetService, Task<T>> action, Guid? tenant = null) =>
        ActiveVendorKit.AsAsync(_host.Services, tenant ?? _tenantA, actor, s => action(s.GetRequiredService<IVerificationRuleSetService>()));

    private static Dictionary<string, RuleSetting> Change(string rule, bool enabled, string severity, params (string, decimal)[] p)
    {
        var rules = new Dictionary<string, RuleSetting>(RuleConfig.Defaults().Rules)
        {
            [rule] = new(enabled, severity, p.Length == 0 ? RuleConfig.Defaults().Get(rule).Parameters : p.ToDictionary(x => x.Item1, x => x.Item2)),
        };
        return rules;
    }

    private static ProposeRuleChange Propose(Dictionary<string, RuleSetting> rules, string note = "Because") => new(rules, note);

    [Fact]
    public async Task An_entity_starts_with_the_built_in_defaults_as_version_zero()
    {
        var rules = await As(_admin, s => s.GetAsync(_entityA));
        Assert.Equal(0, rules.Active.VersionNo);
        Assert.Equal("Default", rules.Active.Status);
        Assert.Null(rules.Pending);
        Assert.Equal(RuleSeverity.Block, rules.Active.Config.Get(RuleCodes.DuplicateExact).Severity);

        var (version, config) = await As(_admin, s => s.ActiveConfigAsync(_entityA));
        Assert.Equal(0, version);
        Assert.Equal(RuleConfig.Defaults().Rules.Count, config.Rules.Count);
    }

    [Fact]
    public async Task A_tightening_change_is_active_at_once_and_becomes_the_next_version()
    {
        var change = await As(_admin, s => s.ProposeAsync(_entityA, Propose(Change(RuleCodes.PoRequired, true, RuleSeverity.Block), "PO is mandatory now")));
        Assert.Equal(RuleSetStatus.Active, change.Status);
        Assert.Equal(1, change.VersionNo);
        Assert.Empty(change.LoosenedRules);

        var (version, config) = await As(_admin, s => s.ActiveConfigAsync(_entityA));
        Assert.Equal(1, version);
        Assert.True(config.Get(RuleCodes.PoRequired).Enabled);
    }

    [Fact]
    public async Task Proposing_the_settings_already_in_force_is_refused()
    {
        await As(_admin, s => s.ProposeAsync(_entityA, Propose(Change(RuleCodes.PoRequired, true, RuleSeverity.Block))));
        var ex = await Assert.ThrowsAsync<ConflictException>(() => As(_admin, s => s.ProposeAsync(_entityA, Propose(Change(RuleCodes.PoRequired, true, RuleSeverity.Block)))));
        Assert.Equal("no_change", ex.Code);
    }

    [Fact]
    public async Task A_loosening_change_waits_for_a_different_person_and_changes_nothing_until_then()
    {
        var change = await As(_admin, s => s.ProposeAsync(_entityA, Propose(Change(RuleCodes.DuplicateExact, true, RuleSeverity.Flag), "Vendor resends invoices often")));
        Assert.Equal(RuleSetStatus.Pending, change.Status);
        Assert.Equal([RuleCodes.DuplicateExact], change.LoosenedRules);

        var rules = await As(_admin, s => s.GetAsync(_entityA));
        Assert.Equal(RuleSeverity.Block, rules.Active.Config.Get(RuleCodes.DuplicateExact).Severity); // still strict
        Assert.Equal(change.Id, rules.Pending!.Id);
        Assert.Equal((0, RuleSeverity.Block), ((await As(_admin, s => s.ActiveConfigAsync(_entityA))).VersionNo, (await As(_admin, s => s.ActiveConfigAsync(_entityA))).Config.Get(RuleCodes.DuplicateExact).Severity));

        var approved = await As(_approver, s => s.ApproveAsync(change.Id!.Value, "OK for this vendor group"));
        Assert.Equal(RuleSetStatus.Active, approved.Status);
        Assert.Equal(_approver, approved.DecidedBy);

        var after = await As(_admin, s => s.GetAsync(_entityA));
        Assert.Equal(RuleSeverity.Flag, after.Active.Config.Get(RuleCodes.DuplicateExact).Severity);
        Assert.Null(after.Pending);
    }

    [Fact]
    public async Task The_person_who_asked_cannot_approve_or_reject_and_the_attempt_is_audited()
    {
        var change = await As(_admin, s => s.ProposeAsync(_entityA, Propose(Change(RuleCodes.AmountTotals, false, RuleSeverity.Block))));
        await Assert.ThrowsAsync<SegregationOfDutiesException>(() => As(_admin, s => s.ApproveAsync(change.Id!.Value, null)));
        await Assert.ThrowsAsync<SegregationOfDutiesException>(() => As(_admin, s => s.RejectAsync(change.Id!.Value, "no")));

        await using var scope = _host.Scope(_tenantA);
        var audit = scope.ServiceProvider.GetRequiredService<Vpms.Audit.Infrastructure.AuditDbContext>();
        Assert.Equal(2, await audit.Events.CountAsync(e => e.Action == "sod.verification-rules.blocked" && e.EntityId == change.Id.ToString()));
        Assert.Equal(RuleSetStatus.Pending, (await As(_admin, s => s.GetAsync(_entityA))).Pending!.Status);
    }

    [Fact]
    public async Task Rejected_and_cancelled_changes_leave_the_rules_alone_and_free_the_slot()
    {
        var first = await As(_admin, s => s.ProposeAsync(_entityA, Propose(Change(RuleCodes.TaxVat, false, RuleSeverity.Flag))));
        Assert.Equal("change_pending", (await Assert.ThrowsAsync<ConflictException>(() => As(_admin, s => s.ProposeAsync(_entityA, Propose(Change(RuleCodes.PoRequired, true, RuleSeverity.Flag)))))).Code);

        var rejected = await As(_approver, s => s.RejectAsync(first.Id!.Value, "Tax checks stay on"));
        Assert.Equal(RuleSetStatus.Rejected, rejected.Status);
        Assert.Equal("Tax checks stay on", rejected.DecisionComment);
        Assert.True((await As(_admin, s => s.GetAsync(_entityA))).Active.Config.Get(RuleCodes.TaxVat).Enabled);

        var second = await As(_admin, s => s.ProposeAsync(_entityA, Propose(Change(RuleCodes.TaxVat, false, RuleSeverity.Flag))));
        Assert.Equal("not_the_maker", (await Assert.ThrowsAsync<ConflictException>(() => As(_approver, s => s.CancelAsync(second.Id!.Value)))).Code);
        Assert.Equal(RuleSetStatus.Cancelled, (await As(_admin, s => s.CancelAsync(second.Id!.Value))).Status);

        // versions keep counting and history keeps everything
        var third = await As(_admin, s => s.ProposeAsync(_entityA, Propose(Change(RuleCodes.PoRequired, true, RuleSeverity.Flag))));
        Assert.Equal(3, third.VersionNo);
        var history = await As(_admin, s => s.HistoryAsync(_entityA));
        Assert.Equal([3, 2, 1], history.Select(h => h.VersionNo));
        Assert.Equal([RuleSetStatus.Active, RuleSetStatus.Cancelled, RuleSetStatus.Rejected], history.Select(h => h.Status));
    }

    [Fact]
    public async Task Approving_retires_the_version_it_replaces_so_only_one_is_ever_active()
    {
        await As(_admin, s => s.ProposeAsync(_entityA, Propose(Change(RuleCodes.PoRequired, true, RuleSeverity.Flag))));
        var loosening = await As(_admin, s => s.ProposeAsync(_entityA, Propose(Change(RuleCodes.PoRequired, false, RuleSeverity.Flag))));
        await As(_approver, s => s.ApproveAsync(loosening.Id!.Value, null));

        var history = await As(_admin, s => s.HistoryAsync(_entityA));
        Assert.Equal([RuleSetStatus.Active, RuleSetStatus.Superseded], history.Select(h => h.Status));
        await Assert.ThrowsAsync<ConflictException>(() => As(_approver, s => s.ApproveAsync(loosening.Id!.Value, null))); // decided already
    }

    [Fact]
    public async Task Entities_have_independent_rules()
    {
        await As(_admin, s => s.ProposeAsync(_entityA, Propose(Change(RuleCodes.PoRequired, true, RuleSeverity.Block))));
        Assert.True((await As(_admin, s => s.GetAsync(_entityA))).Active.Config.Get(RuleCodes.PoRequired).Enabled);
        Assert.False((await As(_admin, s => s.GetAsync(_entityB))).Active.Config.Get(RuleCodes.PoRequired).Enabled);
        Assert.Equal(0, (await As(_admin, s => s.GetAsync(_entityB))).Active.VersionNo);

        var queue = await As(_admin, s => s.ListChangesAsync(null, new HashSet<Guid> { _entityB }));
        Assert.Empty(queue);
    }

    [Fact]
    public async Task The_changes_queue_lists_loosening_changes_with_the_settings_they_would_replace()
    {
        var change = await As(_admin, s => s.ProposeAsync(_entityA, Propose(Change(RuleCodes.PoMatch, true, RuleSeverity.Flag, ("priceTolerancePercent", 10m), ("priceToleranceAbsolute", 0m), ("quantityTolerancePercent", 0m), ("overBillingPercent", 0m)))));
        var queue = await As(_approver, s => s.ListChangesAsync(RuleSetStatus.Pending, null));
        var item = Assert.Single(queue);
        Assert.Equal(change.Id, item.Change.Id);
        Assert.Equal(2m, item.Base.Config.Param(RuleCodes.PoMatch, "priceTolerancePercent"));
        Assert.Equal(10m, item.Change.Config.Param(RuleCodes.PoMatch, "priceTolerancePercent"));
    }

    [Fact]
    public async Task Invalid_changes_are_refused_before_anything_is_stored()
    {
        Assert.Equal("reason_required", (await Assert.ThrowsAsync<ValidationFailedException>(() => As(_admin, s => s.ProposeAsync(_entityA, Propose(Change(RuleCodes.PoRequired, true, RuleSeverity.Flag), " "))))).Code);
        Assert.Equal("parameter_out_of_range", (await Assert.ThrowsAsync<ValidationFailedException>(() =>
            As(_admin, s => s.ProposeAsync(_entityA, Propose(Change(RuleCodes.PoMatch, true, RuleSeverity.Flag, ("priceTolerancePercent", 99m))))))).Code);
        await Assert.ThrowsAsync<NotFoundException>(() => As(_admin, s => s.ProposeAsync(Guid.NewGuid(), Propose(Change(RuleCodes.PoRequired, true, RuleSeverity.Flag)))));
        Assert.Empty(await As(_admin, s => s.HistoryAsync(_entityA)));
    }

    [Fact]
    public async Task Another_tenant_cannot_see_or_decide_rule_changes()
    {
        var change = await As(_admin, s => s.ProposeAsync(_entityA, Propose(Change(RuleCodes.TaxVat, false, RuleSeverity.Flag))));
        Assert.Empty(await As(_approver, s => s.ListChangesAsync(null, null), _tenantB));
        await Assert.ThrowsAsync<NotFoundException>(() => As(_approver, s => s.ApproveAsync(change.Id!.Value, null), _tenantB));
        await Assert.ThrowsAsync<NotFoundException>(() => As(_approver, s => s.GetAsync(_entityA), _tenantB)); // the entity does not exist there
    }

    [Fact]
    public async Task Two_proposals_at_the_same_time_cannot_both_win()
    {
        var a = As(_admin, s => s.ProposeAsync(_entityA, Propose(Change(RuleCodes.TaxVat, false, RuleSeverity.Flag))));
        var b = As(_approver, s => s.ProposeAsync(_entityA, Propose(Change(RuleCodes.DateSanity, false, RuleSeverity.Flag))));
        var results = await Task.WhenAll(Settle(a), Settle(b));
        Assert.Equal(1, results.Count(r => r));
        Assert.Single(await As(_admin, s => s.HistoryAsync(_entityA)));

        static async Task<bool> Settle(Task<RuleSetDto> t)
        {
            try
            {
                await t;
                return true;
            }
            catch (ConflictException)
            {
                return false;
            }
        }
    }
}
