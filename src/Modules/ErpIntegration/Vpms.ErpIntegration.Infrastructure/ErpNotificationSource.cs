using Microsoft.EntityFrameworkCore;
using Vpms.ErpIntegration.Domain;
using Vpms.Identity.Contracts;
using Vpms.Notifications.Contracts;

namespace Vpms.ErpIntegration.Infrastructure;

/// <summary>Messages the ERP refused or that gave up, and reconciliation runs that found new differences, told to the entity's ERP operators.</summary>
public sealed class ErpNotificationSource(ErpDbContext db) : INotificationSource
{
    public string Name => "erp";

    public async Task<IReadOnlyList<NotificationEvent>> PollAsync(DateTimeOffset since, CancellationToken ct = default)
    {
        var events = new List<NotificationEvent>();
        var dead = await db.SyncRecords.AsNoTracking().Where(r => r.Status == SyncStatus.DeadLetter && r.CompletedAt >= since).ToListAsync(ct);
        foreach (var r in dead)
        {
            events.Add(new NotificationEvent(NotificationKinds.ErpDeadLetter, $"erp-dead:{r.Id:N}:{r.CompletedAt!.Value.UtcTicks}", r.LegalEntityId, r.Type, [], Permissions.ErpOperate, "/erp/sync",
                new Dictionary<string, string> { ["detail"] = r.LastErrorCode ?? "" }, r.CompletedAt.Value));
        }

        var runs = await db.ReconRuns.AsNoTracking().Where(r => r.Status == ReconRunStatus.Completed && r.NewItems > 0 && r.CompletedAt >= since).ToListAsync(ct);
        foreach (var r in runs)
        {
            events.Add(new NotificationEvent(NotificationKinds.ReconciliationDifferences, $"recon:{r.Id:N}", r.LegalEntityId, r.StartedAt.UtcDateTime.ToString("yyyy-MM-dd"), [], Permissions.ErpOperate,
                "/erp/reconciliation", new Dictionary<string, string> { ["count"] = r.NewItems.ToString() }, r.CompletedAt!.Value));
        }

        return events;
    }
}
