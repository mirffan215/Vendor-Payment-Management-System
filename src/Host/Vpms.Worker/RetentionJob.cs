using System.Text.Json;
using Vpms.BuildingBlocks.Audit;
using Vpms.BuildingBlocks.Jobs;
using Vpms.BuildingBlocks.Tenancy;
using Vpms.CustomerAdmin.Contracts;
using Vpms.ErpIntegration.Domain;

namespace Vpms.Worker;

/// <summary>
/// Deletes operational data (notifications, workflow notices) that has passed the tenant's retention period, tenant by tenant, in small batches.
/// Anything under a legal hold is left alone, and every run that deleted something is audited. Financial records are never touched (ADR-0024).
/// </summary>
public sealed class RetentionJob(IServiceScopeFactory scopes, IConfiguration configuration, ILogger<RetentionJob> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(configuration.GetValue("Retention:SweepMinutes", 360)));
        do
        {
            try
            {
                await JobTelemetry.RunAsync(GetType().Name, () => RunOnceAsync(stoppingToken));
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Retention sweep failed.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        IReadOnlyList<TenantDefaults> tenants;
        await using (var list = scopes.CreateAsyncScope())
        {
            tenants = await list.ServiceProvider.GetRequiredService<ITenantLookup>().ListActiveAsync(ct);
        }

        var batch = configuration.GetValue("Retention:Batch", 500);
        var total = 0;
        foreach (var t in tenants)
        {
            try
            {
                total += await RunTenantAsync(t.TenantId, batch, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Retention sweep failed for tenant {TenantId}.", t.TenantId);
            }
        }

        return total;
    }

    private async Task<int> RunTenantAsync(Guid tenantId, int batch, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().SetTenant(tenantId);
        var actor = scope.ServiceProvider.GetRequiredService<AuditContext>();
        actor.ActorId = ErpSystemActor.Id;
        actor.ActorType = AuditActorType.System;
        actor.CorrelationId = "retention-" + Guid.NewGuid().ToString("N")[..12];

        var context = await scope.ServiceProvider.GetRequiredService<IRetentionPolicyReader>().ContextAsync(ct);
        if (context.TenantHeld) return 0;

        var audit = scope.ServiceProvider.GetRequiredService<IAuditPort>();
        var total = 0;
        foreach (var target in scope.ServiceProvider.GetServices<IRetentionTarget>().Where(t => t.CanDispose))
        {
            var removed = 0;
            int step;
            do
            {
                step = await target.DisposeAsync(context, batch, ct);
                removed += step;
            }
            while (step >= batch && !ct.IsCancellationRequested);

            if (removed == 0) continue;
            total += removed;
            await audit.WriteNowAsync(new AuditEntry("retention.disposed", "RetentionRun", target.DataClass, null,
                JsonSerializer.Serialize(new { dataClass = target.DataClass, removed, cutoff = context.CutoffFor(target.DataClass), months = context.MonthsFor(target.DataClass) })), ct);
        }

        return total;
    }
}
