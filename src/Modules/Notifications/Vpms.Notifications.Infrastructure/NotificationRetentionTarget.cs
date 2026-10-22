using Microsoft.EntityFrameworkCore;
using Vpms.BuildingBlocks.Jobs;

namespace Vpms.Notifications.Infrastructure;

/// <summary>In-app notifications (and their delivery rows) older than the tenant's retention for them. Not a financial record: deleted automatically.</summary>
public sealed class NotificationRetentionTarget(NotificationsDbContext db) : IRetentionTarget
{
    public string DataClass => RetentionClasses.Notifications;

    public bool CanDispose => true;

    public Task<int> CountEligibleAsync(RetentionContext context, CancellationToken ct = default)
    {
        var cutoff = context.CutoffFor(DataClass);
        return db.Notifications.AsNoTracking().CountAsync(n => n.CreatedAt < cutoff, ct);
    }

    public async Task<int> DisposeAsync(RetentionContext context, int batch, CancellationToken ct = default)
    {
        if (context.TenantHeld) return 0;
        var cutoff = context.CutoffFor(DataClass);
        var ids = await db.Notifications.Where(n => n.CreatedAt < cutoff).OrderBy(n => n.CreatedAt).Select(n => n.Id).Take(batch).ToListAsync(ct);
        if (ids.Count == 0) return 0;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Deliveries.Where(d => ids.Contains(d.NotificationId)).ExecuteDeleteAsync(ct);
        var deleted = await db.Notifications.Where(n => ids.Contains(n.Id)).ExecuteDeleteAsync(ct);
        await tx.CommitAsync(ct);
        return deleted;
    }
}
