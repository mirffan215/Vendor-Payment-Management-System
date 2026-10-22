using Vpms.BuildingBlocks.Tenancy;
using Vpms.Workflow.Application;

namespace Vpms.Worker;

/// <summary>Sends approval reminders and escalates overdue approval tasks, tenant by tenant.</summary>
public sealed class WorkflowTimerJob(IServiceScopeFactory scopes, IConfiguration configuration, ILogger<WorkflowTimerJob> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(configuration.GetValue("Workflow:TimerSeconds", 60)));
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
                logger.LogError(ex, "Workflow timer run failed.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task<TimerRunResult> RunOnceAsync(CancellationToken ct)
    {
        IReadOnlyList<Guid> tenants;
        await using (var list = scopes.CreateAsyncScope())
        {
            tenants = await list.ServiceProvider.GetRequiredService<IWorkflowTimers>().TenantsWithDueWorkAsync(ct);
        }

        int reminders = 0, escalations = 0;
        foreach (var tenantId in tenants)
        {
            await using var scope = scopes.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<TenantContext>().SetTenant(tenantId);
            var r = await scope.ServiceProvider.GetRequiredService<IWorkflowTimers>().RunDueAsync(ct);
            (reminders, escalations) = (reminders + r.Reminders, escalations + r.Escalations);
        }

        if (reminders + escalations > 0) logger.LogInformation("Approval deadlines: {Reminders} reminders, {Escalations} escalations.", reminders, escalations);
        return new TimerRunResult(reminders, escalations);
    }
}
