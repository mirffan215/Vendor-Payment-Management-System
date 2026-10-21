using Vpms.BuildingBlocks.Tenancy;
using Vpms.Invoices.Application;

namespace Vpms.Worker;

/// <summary>Polls the invoice mailbox of every enabled legal entity and turns new mail into draft invoices.</summary>
public sealed class EmailIntakeJob(IServiceScopeFactory scopes, IConfiguration configuration, ILogger<EmailIntakeJob> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(configuration.GetValue("Invoices:MailPollSeconds", 60)));
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
                logger.LogError(ex, "Invoice mailbox polling failed.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task<MailboxRunResult> RunOnceAsync(CancellationToken ct)
    {
        IReadOnlyList<(Guid TenantId, Guid LegalEntityId)> mailboxes;
        await using (var list = scopes.CreateAsyncScope())
        {
            mailboxes = await list.ServiceProvider.GetRequiredService<IIntakeScan>().EnabledMailboxesAsync(ct);
        }

        int fetched = 0, created = 0, unmatched = 0, skipped = 0, failed = 0;
        foreach (var (tenantId, entityId) in mailboxes)
        {
            await using var scope = scopes.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<TenantContext>().SetTenant(tenantId);
            var r = await scope.ServiceProvider.GetRequiredService<IInvoiceIntakeService>().ProcessMailboxAsync(entityId, ct);
            (fetched, created, unmatched, skipped, failed) = (fetched + r.Fetched, created + r.Created, unmatched + r.Unmatched, skipped + r.Skipped, failed + r.Failed);
        }

        if (fetched > 0) logger.LogInformation("Invoice mail: {Fetched} fetched, {Created} drafts, {Unmatched} unmatched, {Skipped} replays, {Failed} failed.", fetched, created, unmatched, skipped, failed);
        return new MailboxRunResult(fetched, created, unmatched, skipped, failed);
    }
}

/// <summary>Runs the OCR-ready extractor over newly scanned invoice files. Output is stored as suggestions only.</summary>
public sealed class InvoiceExtractionJob(IServiceScopeFactory scopes, IConfiguration configuration, ILogger<InvoiceExtractionJob> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(configuration.GetValue("Invoices:ExtractionSeconds", 30)));
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
                logger.LogError(ex, "Invoice extraction run failed.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        IReadOnlyList<Guid> tenants;
        await using (var list = scopes.CreateAsyncScope())
        {
            tenants = await list.ServiceProvider.GetRequiredService<IIntakeScan>().TenantsWithPendingExtractionAsync(ct);
        }

        var done = 0;
        foreach (var tenantId in tenants)
        {
            await using var scope = scopes.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<TenantContext>().SetTenant(tenantId);
            done += await scope.ServiceProvider.GetRequiredService<IInvoiceExtractionService>().RunPendingAsync(20, ct);
        }

        return done;
    }
}

/// <summary>Safety net: verifies invoices that were received but not verified (for example if the request was interrupted).</summary>
public sealed class InvoiceVerificationJob(IServiceScopeFactory scopes, IConfiguration configuration, ILogger<InvoiceVerificationJob> logger) : BackgroundService
{
    private static readonly TimeSpan Grace = TimeSpan.FromMinutes(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(configuration.GetValue("Invoices:VerificationSweepSeconds", 120)));
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
                logger.LogError(ex, "Invoice verification sweep failed.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        IReadOnlyList<Guid> tenants;
        await using (var list = scopes.CreateAsyncScope())
        {
            tenants = await list.ServiceProvider.GetRequiredService<IVerificationScan>().TenantsWithUnverifiedInvoicesAsync(Grace, ct);
        }

        var done = 0;
        foreach (var tenantId in tenants)
        {
            await using var scope = scopes.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<TenantContext>().SetTenant(tenantId);
            done += await scope.ServiceProvider.GetRequiredService<IVerificationScan>().VerifyStaleAsync(Grace, 50, ct);
        }

        if (done > 0) logger.LogInformation("Verified {Count} invoices that had been left waiting.", done);
        return done;
    }
}
