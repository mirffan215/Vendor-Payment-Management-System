using Vpms.BuildingBlocks.Tenancy;
using Vpms.Documents.Application;

namespace Vpms.Worker;

/// <summary>Scans quarantined uploads. Each attachment is handled in its own scope with its tenant established.</summary>
public sealed class AttachmentScanJob(IServiceScopeFactory scopes, IConfiguration configuration, ILogger<AttachmentScanJob> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(configuration.GetValue("Documents:ScanIntervalSeconds", 10)));
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
                logger.LogError(ex, "Attachment scan run failed.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task<int> RunOnceAsync(CancellationToken ct, int batch = 50)
    {
        IReadOnlyList<PendingScan> pending;
        await using (var list = scopes.CreateAsyncScope())
        {
            pending = await list.ServiceProvider.GetRequiredService<IAttachmentScanService>().ListPendingAsync(batch, ct);
        }

        foreach (var item in pending)
        {
            await using var scope = scopes.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<TenantContext>().SetTenant(item.TenantId);
            try
            {
                await scope.ServiceProvider.GetRequiredService<IAttachmentScanService>().ScanAsync(item.AttachmentId, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One bad attachment must not stop the others; it stays Pending and is retried.
                logger.LogError(ex, "Scanning attachment {AttachmentId} failed.", item.AttachmentId);
            }
        }

        return pending.Count;
    }
}
