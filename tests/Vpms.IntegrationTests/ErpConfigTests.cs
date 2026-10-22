using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vpms.BuildingBlocks.Errors;
using Vpms.BuildingBlocks.Security;
using Vpms.ErpIntegration.Application;
using Vpms.ErpIntegration.Domain;
using Vpms.ErpIntegration.Infrastructure;
using Vpms.TestSupport;

namespace Vpms.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class ErpConfigTests : IAsyncLifetime
{
    private readonly SqlServerFixture _sql;
    private WorkflowWorld _w = null!;
    private Guid _entityA, _entityOfB;
    private Guid _configurer, _approver;

    public ErpConfigTests(SqlServerFixture sql) => _sql = sql;

    public async Task InitializeAsync()
    {
        _w = new WorkflowWorld(new IntegrationHost(await _sql.CreateDatabaseAsync()));
        await _w.InitializeAsync();
        _entityA = await _w.EntityAsync(_w.TenantA, "A1");
        _entityOfB = await _w.EntityAsync(_w.TenantB, "B1");
        _configurer = await _w.UserAsync(_w.TenantA, "erpcfg@t.com", null, "ERP Administrator");
        _approver = await _w.UserAsync(_w.TenantA, "erpapp@t.com", null, "ERP Change Approver");
    }

    public async Task DisposeAsync() => await _w.Host.DisposeAsync();

    private Task<T> Erp<T>(Guid tenant, Guid actor, Func<IErpConfigService, Task<T>> action) => _w.AsAsync(tenant, actor, s => action(s.GetRequiredService<IErpConfigService>()));

    internal static ConnectionConfig Connection(string secretRef = "erp-secret") =>
        new(ErpProviders.Mock, "https://erp.example.test", "tenant", "client", secretRef, new Dictionary<string, string>());

    internal static MappingConfig Mapping(string company = "USMF") =>
        new(company, "VENDINV", null, new Dictionary<string, string>(), new Dictionary<string, string>(), []);

    private async Task ActivateConnectionAsync(string secretRef = "erp-secret")
    {
        var c = await Erp(_w.TenantA, _configurer, s => s.ProposeConnectionAsync(Connection(secretRef), "Initial connection"));
        await Erp(_w.TenantA, _approver, s => s.ApproveAsync(c.Id!.Value, "ok"));
    }

    private async Task ActivateMappingAsync()
    {
        var m = await Erp(_w.TenantA, _configurer, s => s.ProposeMappingAsync(_entityA, Mapping(), "Initial mapping"));
        await Erp(_w.TenantA, _approver, s => s.ApproveAsync(m.Id!.Value, "ok"));
    }

    [Fact]
    public async Task A_connection_is_inactive_until_a_different_person_approves_it()
    {
        var proposed = await Erp(_w.TenantA, _configurer, s => s.ProposeConnectionAsync(Connection(), "Initial connection"));
        Assert.Equal(ConfigStatus.Pending, proposed.Status);
        Assert.Null((await Erp(_w.TenantA, _configurer, s => s.GetConnectionAsync())).Active);

        var ex = await Assert.ThrowsAsync<SegregationOfDutiesException>(() => Erp(_w.TenantA, _configurer, s => s.ApproveAsync(proposed.Id!.Value, null)));
        Assert.Equal("maker-checker", ex.Rule);

        var active = await Erp(_w.TenantA, _approver, s => s.ApproveAsync(proposed.Id!.Value, "ok"));
        Assert.Equal(ConfigStatus.Active, active.Status);
        Assert.Equal(_approver, active.DecidedBy);
        Assert.NotNull((await Erp(_w.TenantA, _configurer, s => s.GetConnectionAsync())).Active);
    }

    [Fact]
    public async Task The_attempt_to_approve_your_own_change_is_audited()
    {
        var p = await Erp(_w.TenantA, _configurer, s => s.ProposeConnectionAsync(Connection(), "Initial connection"));
        await Assert.ThrowsAsync<SegregationOfDutiesException>(() => Erp(_w.TenantA, _configurer, s => s.ApproveAsync(p.Id!.Value, null)));
        var count = await _w.AsAsync(_w.TenantA, _w.Admin, async s =>
            await s.GetRequiredService<Vpms.Audit.Infrastructure.AuditDbContext>().Events.CountAsync(e => e.Action == "sod.erp-change.blocked" && e.EntityId == p.Id.ToString()));
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task A_new_version_supersedes_the_active_one_and_the_history_is_kept()
    {
        await ActivateConnectionAsync("secret-one");
        var v2 = await Erp(_w.TenantA, _configurer, s => s.ProposeConnectionAsync(Connection("secret-two"), "Rotate"));
        Assert.Equal(2, v2.VersionNo);
        await Erp(_w.TenantA, _approver, s => s.ApproveAsync(v2.Id!.Value, null));

        var state = await Erp(_w.TenantA, _configurer, s => s.GetConnectionAsync());
        Assert.Equal("secret-two", state.Active!.Connection!.SecretRef);
        var changes = await Erp(_w.TenantA, _approver, s => s.ListChangesAsync(ConfigStatus.Superseded, null, true));
        Assert.Single(changes);
        Assert.Equal("secret-one", changes[0].Change.Connection!.SecretRef);
    }

    [Fact]
    public async Task Only_one_change_can_wait_and_an_identical_configuration_is_refused()
    {
        await ActivateConnectionAsync();
        var first = await Erp(_w.TenantA, _configurer, s => s.ProposeConnectionAsync(Connection("other"), "Change"));
        var pending = await Assert.ThrowsAsync<ConflictException>(() => Erp(_w.TenantA, _configurer, s => s.ProposeConnectionAsync(Connection("another"), "Again")));
        Assert.Equal("change_pending", pending.Code);

        await Erp(_w.TenantA, _configurer, s => s.CancelAsync(first.Id!.Value));
        var same = await Assert.ThrowsAsync<ConflictException>(() => Erp(_w.TenantA, _configurer, s => s.ProposeConnectionAsync(Connection(), "Same")));
        Assert.Equal("no_change", same.Code);
    }

    [Fact]
    public async Task A_rejected_change_needs_a_reason_and_never_becomes_active()
    {
        var p = await Erp(_w.TenantA, _configurer, s => s.ProposeConnectionAsync(Connection(), "Initial"));
        await Assert.ThrowsAsync<ValidationFailedException>(() => Erp(_w.TenantA, _approver, s => s.RejectAsync(p.Id!.Value, " ")));
        var rejected = await Erp(_w.TenantA, _approver, s => s.RejectAsync(p.Id!.Value, "Wrong URL"));
        Assert.Equal(ConfigStatus.Rejected, rejected.Status);
        Assert.Null((await Erp(_w.TenantA, _configurer, s => s.GetConnectionAsync())).Active);
    }

    [Fact]
    public async Task Invalid_configuration_is_refused_before_it_is_stored()
    {
        var bad = await Assert.ThrowsAsync<ValidationFailedException>(() => Erp(_w.TenantA, _configurer, s => s.ProposeConnectionAsync(Connection("not a vault name!"), "x")));
        Assert.Equal("secret_ref_invalid", bad.Code);
        await Assert.ThrowsAsync<ValidationFailedException>(() => Erp(_w.TenantA, _configurer, s => s.ProposeMappingAsync(_entityA, Mapping(""), "x")));
        await Assert.ThrowsAsync<NotFoundException>(() => Erp(_w.TenantA, _configurer, s => s.ProposeMappingAsync(Guid.NewGuid(), Mapping(), "x")));
        await Assert.ThrowsAsync<ValidationFailedException>(() => Erp(_w.TenantA, _configurer, s => s.ProposeConnectionAsync(Connection(), " ")));
    }

    [Fact]
    public async Task The_stored_row_holds_only_the_secret_reference_never_the_secret()
    {
        await ActivateConnectionAsync();
        var json = await _w.AsAsync(_w.TenantA, _w.Admin, async s => await s.GetRequiredService<ErpDbContext>().Configs.AsNoTracking().Select(c => c.ConfigJson).FirstAsync());
        Assert.Contains("erp-secret", json);
        Assert.DoesNotContain(TestSecrets.Value, json);

        // and a test of the connection returns neither the secret nor leaves it in the audit trail
        await ActivateMappingAsync();
        var result = await Erp(_w.TenantA, _configurer, s => s.TestAsync(_entityA));
        Assert.True(result.Ok);
        Assert.DoesNotContain(TestSecrets.Value, JsonSerializer.Serialize(result));
        var audit = await _w.AsAsync(_w.TenantA, _w.Admin, async s =>
            await s.GetRequiredService<Vpms.Audit.Infrastructure.AuditDbContext>().Events.AsNoTracking().Select(e => e.Action + e.ChangeSummaryJson).ToListAsync());
        Assert.DoesNotContain(audit, a => a.Contains(TestSecrets.Value));
    }

    [Fact]
    public async Task Testing_the_connection_explains_what_is_missing_without_calling_the_erp()
    {
        Assert.Equal("connection_missing", (await Erp(_w.TenantA, _configurer, s => s.TestAsync(_entityA))).ErrorCode);
        await ActivateConnectionAsync();
        Assert.Equal("mapping_missing", (await Erp(_w.TenantA, _configurer, s => s.TestAsync(_entityA))).ErrorCode);
        Assert.Equal(0, _w.Host.Erp.CountCalls("ValidateConnection"));
    }

    [Fact]
    public async Task A_secret_the_vault_cannot_supply_fails_the_test_clearly()
    {
        await ActivateConnectionAsync("missing-secret");
        await ActivateMappingAsync();
        var result = await Erp(_w.TenantA, _configurer, s => s.TestAsync(_entityA));
        Assert.False(result.Ok);
        Assert.Equal("secret_unavailable", result.ErrorCode);
        Assert.Equal(0, _w.Host.Erp.CountCalls("ValidateConnection"));
    }

    [Fact]
    public async Task A_reported_erp_failure_is_returned_to_the_caller()
    {
        await ActivateConnectionAsync();
        await ActivateMappingAsync();
        _w.Host.Erp.Script("ValidateConnection", Vpms.ErpIntegration.Contracts.ErpResult.Rejected("auth_failed", "Authentication failed"));
        var result = await Erp(_w.TenantA, _configurer, s => s.TestAsync(_entityA));
        Assert.False(result.Ok);
        Assert.Equal("auth_failed", result.ErrorCode);
    }

    [Fact]
    public async Task One_tenant_cannot_see_or_decide_another_tenants_erp_configuration()
    {
        await ActivateConnectionAsync();
        var pendingA = await Erp(_w.TenantA, _configurer, s => s.ProposeMappingAsync(_entityA, Mapping(), "Initial mapping"));

        var userB = await _w.UserAsync(_w.TenantB, "erpb@t.com", null, "ERP Change Approver");
        Assert.Null((await Erp(_w.TenantB, userB, s => s.GetConnectionAsync())).Active);
        Assert.Empty(await Erp(_w.TenantB, userB, s => s.ListChangesAsync(null, null, true)));
        await Assert.ThrowsAsync<NotFoundException>(() => Erp(_w.TenantB, userB, s => s.ApproveAsync(pendingA.Id!.Value, null)));
        await Assert.ThrowsAsync<NotFoundException>(() => Erp(_w.TenantB, userB, s => s.GetMappingAsync(_entityA)));
        Assert.NotEqual(Guid.Empty, _entityOfB);
    }

    [Fact]
    public async Task Entity_scoped_listing_hides_other_entities_and_tenant_wide_changes_when_not_entitled()
    {
        await Erp(_w.TenantA, _configurer, s => s.ProposeConnectionAsync(Connection(), "Initial"));
        await Erp(_w.TenantA, _configurer, s => s.ProposeMappingAsync(_entityA, Mapping(), "Initial mapping"));
        var otherEntity = await _w.EntityAsync(_w.TenantA, "A2");
        await Erp(_w.TenantA, _configurer, s => s.ProposeMappingAsync(otherEntity, Mapping("DEMF"), "Initial mapping"));

        var scoped = await Erp(_w.TenantA, _approver, s => s.ListChangesAsync(null, new HashSet<Guid> { _entityA }, false));
        Assert.Single(scoped);
        Assert.Equal(_entityA, scoped[0].Change.LegalEntityId);
        Assert.Equal(3, (await Erp(_w.TenantA, _approver, s => s.ListChangesAsync(null, null, true))).Count);
    }
}
