using Vpms.BuildingBlocks.Audit;
using Vpms.BuildingBlocks.Tenancy;
using Vpms.CustomerAdmin.Contracts;
using Vpms.ErpIntegration.Domain;
using Vpms.Notifications.Application;

namespace Vpms.Worker;

/// <summary>Turns what happened in each module into notifications for the people it concerns, tenant by tenant.</summary>
public sealed class NotificationJob(IServiceScopeFactory scopes, IConfiguration configuration, ILogger<NotificationJob> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(configuration.GetValue("Notifications:SweepSeconds", 60)));
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
                logger.LogError(ex, "Notification sweep failed.");
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

        var created = 0;
        foreach (var t in tenants)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                scope.ServiceProvider.GetRequiredService<TenantContext>().SetTenant(t.TenantId);
                var actor = scope.ServiceProvider.GetRequiredService<AuditContext>();
                actor.ActorId = ErpSystemActor.Id;
                actor.ActorType = AuditActorType.System;
                var result = await scope.ServiceProvider.GetRequiredService<INotificationSweep>().RunAsync(ct);
                var delivered = await scope.ServiceProvider.GetRequiredService<INotificationDelivery>().ProcessDueAsync(configuration.GetValue("Notifications:DeliveryBatch", 50), ct);
                if (delivered.DeadLettered > 0) logger.LogWarning("Notification deliveries dead-lettered for tenant {TenantId}: {Count}.", t.TenantId, delivered.DeadLettered);
                created += result.Created;
                foreach (var source in result.FailedSources) logger.LogWarning("Notification source {Source} failed for tenant {TenantId}.", source, t.TenantId);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Notification sweep failed for tenant {TenantId}.", t.TenantId);
            }
        }

        return created;
    }
}
