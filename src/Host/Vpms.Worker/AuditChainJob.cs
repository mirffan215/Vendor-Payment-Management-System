using Vpms.Audit.Application;

namespace Vpms.Worker;

/// <summary>
/// Periodically anchors every tenant's chain head outside the database and re-verifies the whole chain.
/// A broken chain is logged at Critical so alerting can page the on-call engineer.
/// </summary>
public sealed class AuditChainJob(IServiceScopeFactory scopes, IConfiguration configuration, ILogger<AuditChainJob> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMinutes(configuration.GetValue("Audit:VerifyIntervalMinutes", 60));
        using var timer = new PeriodicTimer(interval);
        do
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Audit chain job failed.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task RunOnceAsync(CancellationToken ct)
    {
        await using var listScope = scopes.CreateAsyncScope();
        var tenants = await listScope.ServiceProvider.GetRequiredService<IAuditChainVerifier>().ListTenantsWithEventsAsync(ct);

        foreach (var tenantId in tenants)
        {
            // One scope per tenant so a failure for one tenant never affects another.
            await using var scope = scopes.CreateAsyncScope();
            var result = await scope.ServiceProvider.GetRequiredService<IAuditChainVerifier>().VerifyAsync(tenantId, ct);
            if (result.IsValid)
            {
                await scope.ServiceProvider.GetRequiredService<IAuditAnchorService>().AnchorAsync(tenantId, ct);
                logger.LogInformation("Audit chain verified for tenant {TenantId}: {Events} events.", tenantId, result.EventsChecked);
            }
            else
            {
                logger.LogCritical("AUDIT CHAIN BROKEN for tenant {TenantId} at sequence {Sequence}: {Problem}",
                    tenantId, result.FirstBrokenSequence, result.Problem);
            }
        }
    }
}
