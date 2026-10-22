using Vpms.BuildingBlocks.Tenancy;
using Vpms.Payments.Application;

namespace Vpms.Worker;

/// <summary>
/// Re-checks open payment proposals against their invoices, vendors and bank details, tenant by tenant. Changes are normally caught the
/// moment they happen; this is the safety net, and it also catches changes made outside the usual paths.
/// </summary>
public sealed class PaymentIntegrityJob(IServiceScopeFactory scopes, IConfiguration configuration, ILogger<PaymentIntegrityJob> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(configuration.GetValue("Payments:IntegritySweepSeconds", 120)));
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
                logger.LogError(ex, "Payment integrity sweep failed.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        IReadOnlyList<Guid> tenants;
        await using (var list = scopes.CreateAsyncScope())
        {
            tenants = await list.ServiceProvider.GetRequiredService<IPaymentScan>().TenantsWithOpenProposalsAsync(ct);
        }

        var invalidated = 0;
        foreach (var tenantId in tenants)
        {
            await using var scope = scopes.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<TenantContext>().SetTenant(tenantId);
            invalidated += await scope.ServiceProvider.GetRequiredService<IPaymentProposalService>().SweepAsync(ct);
        }

        if (invalidated > 0) logger.LogWarning("Payment integrity sweep invalidated {Count} proposals.", invalidated);
        return invalidated;
    }
}
