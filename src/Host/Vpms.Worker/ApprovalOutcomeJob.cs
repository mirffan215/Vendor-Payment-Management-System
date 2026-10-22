using Vpms.BuildingBlocks.Tenancy;
using Vpms.Workflow.Application;

namespace Vpms.Worker;

/// <summary>Tells subject modules about finished approvals whose outcome could not be applied at the time (the decision itself is already stored).</summary>
public sealed class ApprovalOutcomeJob(IServiceScopeFactory scopes, IConfiguration configuration, ILogger<ApprovalOutcomeJob> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(configuration.GetValue("Workflow:OutcomeSweepSeconds", 30)));
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
                logger.LogError(ex, "Approval outcome sweep failed.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        IReadOnlyList<Guid> tenants;
        await using (var list = scopes.CreateAsyncScope())
        {
            tenants = await list.ServiceProvider.GetRequiredService<IApprovalScan>().TenantsWithPendingOutcomesAsync(ct);
        }

        var applied = 0;
        foreach (var tenantId in tenants)
        {
            await using var scope = scopes.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<TenantContext>().SetTenant(tenantId);
            applied += await scope.ServiceProvider.GetRequiredService<IApprovalService>().ApplyPendingOutcomesAsync(ct);
        }

        if (applied > 0) logger.LogInformation("Applied {Count} approval outcomes.", applied);
        return applied;
    }
}
