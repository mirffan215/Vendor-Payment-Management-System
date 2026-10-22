using Vpms.BuildingBlocks.Audit;
using Vpms.BuildingBlocks.Tenancy;
using Vpms.ErpIntegration.Application;
using Vpms.ErpIntegration.Domain;

namespace Vpms.Worker;

/// <summary>
/// Queues approved invoices and changed vendors for the ERP, sends what is due, and now and then pulls what the ERP knows, tenant by tenant. Several workers can run at once:
/// the queue's leases and idempotency keys keep each message to one active sender and one ERP document.
/// </summary>
public sealed class ErpOutboundJob(IServiceScopeFactory scopes, IConfiguration configuration, ILogger<ErpOutboundJob> logger) : BackgroundService
{
    private DateTimeOffset _nextInbound = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(configuration.GetValue("Erp:OutboundSeconds", 30)));
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
                logger.LogError(ex, "ERP outbound run failed.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task<SyncRunResult> RunOnceAsync(CancellationToken ct)
    {
        IReadOnlyList<Guid> tenants;
        await using (var list = scopes.CreateAsyncScope())
        {
            tenants = await list.ServiceProvider.GetRequiredService<IErpScan>().TenantsWithConnectionAsync(ct);
        }

        int enqueued = 0, processed = 0, succeeded = 0, retrying = 0, dead = 0;
        var pullInbound = DateTimeOffset.UtcNow >= _nextInbound;
        if (pullInbound) _nextInbound = DateTimeOffset.UtcNow.AddSeconds(configuration.GetValue("Erp:InboundSeconds", 300));
        var batch = configuration.GetValue("Erp:OutboundBatch", 50);
        foreach (var tenantId in tenants)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                scope.ServiceProvider.GetRequiredService<TenantContext>().SetTenant(tenantId);

                // Work done by the integration itself is attributed to a fixed system actor, never to a person.
                var actor = scope.ServiceProvider.GetRequiredService<AuditContext>();
                actor.ActorId = ErpSystemActor.Id;
                actor.ActorType = AuditActorType.System;
                var outbound = scope.ServiceProvider.GetRequiredService<IErpOutbound>();
                enqueued += await outbound.EnqueueAsync(ct);
                var run = await outbound.ProcessDueAsync(batch, ct);
                processed += run.Processed;
                succeeded += run.Succeeded;
                retrying += run.Retrying;
                dead += run.DeadLettered;

                if (pullInbound)
                {
                    var inbound = await scope.ServiceProvider.GetRequiredService<IErpInbound>().PullAsync(ct);
                    foreach (var error in inbound.PullErrors) logger.LogWarning("ERP inbound pull for tenant {TenantId}: {Error}", tenantId, error);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One tenant's failure never stops the others.
                logger.LogError(ex, "ERP outbound run failed for tenant {TenantId}.", tenantId);
            }
        }

        if (dead > 0) logger.LogWarning("ERP outbound: {Dead} messages were dead-lettered and need attention.", dead);
        return new SyncRunResult(enqueued, processed, succeeded, retrying, dead);
    }
}
