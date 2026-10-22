using Vpms.BuildingBlocks.Tenancy;
using Vpms.Vendors.Application;

namespace Vpms.Worker;

/// <summary>Expires bank-detail change requests that nobody decided in time, tenant by tenant.</summary>
public sealed class BankChangeExpiryJob(IServiceScopeFactory scopes, IConfiguration configuration, ILogger<BankChangeExpiryJob> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(configuration.GetValue("Vendors:ExpiryIntervalMinutes", 15)));
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
                logger.LogError(ex, "Bank-detail change expiry run failed.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        IReadOnlyList<Guid> tenants;
        await using (var list = scopes.CreateAsyncScope())
        {
            tenants = await list.ServiceProvider.GetRequiredService<IBankChangeExpiryScan>().TenantsWithStaleChangesAsync(ct);
        }

        var expired = 0;
        foreach (var tenantId in tenants)
        {
            await using var scope = scopes.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<TenantContext>().SetTenant(tenantId);
            expired += await scope.ServiceProvider.GetRequiredService<IBankDetailsService>().ExpireStaleAsync(ct);
        }

        if (expired > 0) logger.LogInformation("Expired {Count} stale bank-detail change requests.", expired);
        return expired;
    }
}
