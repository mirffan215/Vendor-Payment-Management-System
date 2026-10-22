using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vpms.ErpIntegration.Application;
using Vpms.ErpIntegration.Domain;
using Vpms.ErpIntegration.Infrastructure;
using Vpms.TestSupport;

namespace Vpms.IntegrationTests;

/// <summary>Payment scenario plus an approved ERP connection and mapping for the entity, and helpers to run the outbound queue as the worker does.</summary>
public abstract class ErpScenarioBase(SqlServerFixture sql) : PaymentScenarioBase(sql)
{
    protected Guid ErpCfg, ErpApprover, ErpOp;

    protected MockErpAdapter Erp => W.Host.Erp;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        ErpCfg = await W.UserAsync(W.TenantA, "erpcfg@t.com", null, "ERP Administrator");
        ErpApprover = await W.UserAsync(W.TenantA, "erpapp@t.com", null, "ERP Change Approver");
        ErpOp = await W.UserAsync(W.TenantA, "erpop@t.com", null, "ERP Operator");
    }

    protected Task<T> Cfg_<T>(Guid actor, Func<IErpConfigService, Task<T>> action) => W.AsAsync(W.TenantA, actor, s => action(s.GetRequiredService<IErpConfigService>()));

    protected Task<T> Sync<T>(Guid actor, Func<IErpSyncService, Task<T>> action) => W.AsAsync(W.TenantA, actor, s => action(s.GetRequiredService<IErpSyncService>()));

    protected async Task ConnectErpAsync(MappingConfig? mapping = null, string secretRef = "erp-secret")
    {
        var c = await Cfg_(ErpCfg, s => s.ProposeConnectionAsync(ErpConfigTests.Connection(secretRef), "connect"));
        await Cfg_(ErpApprover, s => s.ApproveAsync(c.Id!.Value, "ok"));
        var m = await Cfg_(ErpCfg, s => s.ProposeMappingAsync(Entity, mapping ?? ErpConfigTests.Mapping(), "map"));
        await Cfg_(ErpApprover, s => s.ApproveAsync(m.Id!.Value, "ok"));
    }

    /// <summary>One worker cycle for the tenant: queue what is new, then send what is due.</summary>
    protected async Task<(int Enqueued, SyncRunResult Run)> CycleAsync()
    {
        var enqueued = await W.AsAsync(W.TenantA, W.Admin, s => s.GetRequiredService<IErpOutbound>().EnqueueAsync());
        var run = await W.AsAsync(W.TenantA, W.Admin, s => s.GetRequiredService<IErpOutbound>().ProcessDueAsync(50));
        return (enqueued, run);
    }

    /// <summary>Cycles, moving the clock forward each time, until every message is finished (or the cap is reached).</summary>
    protected async Task SettleAsync(int max = 12, TimeSpan? step = null)
    {
        for (var i = 0; i < max; i++)
        {
            var (enqueued, run) = await CycleAsync();
            Clock.Advance(step ?? TimeSpan.FromMinutes(3));
            if (enqueued == 0 && run.Processed == 0 && !(await Records()).Any(r => r.Status is SyncStatus.Pending or SyncStatus.InFlight)) return;
        }
    }

    protected Task<List<ErpSyncRecord>> Records(string? type = null) =>
        W.AsAsync(W.TenantA, W.Admin, s => s.GetRequiredService<ErpDbContext>().SyncRecords.AsNoTracking().Where(r => type == null || r.Type == type).OrderBy(r => r.CreatedAt).ToListAsync());

    protected Task Tamper(Func<ErpDbContext, Task> change) =>
        W.AsAsync(W.TenantA, W.Admin, async s =>
        {
            var db = s.GetRequiredService<ErpDbContext>();
            await change(db);
            await db.SaveChangesAsync();
            return true;
        });
}
