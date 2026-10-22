using Microsoft.EntityFrameworkCore;
using Vpms.BuildingBlocks.Jobs;

namespace Vpms.Workflow.Infrastructure;

/// <summary>Reminder and escalation notices older than the tenant's retention for them. The approval decisions themselves are financial records and stay.</summary>
public sealed class WorkflowNoticeRetentionTarget(WorkflowDbContext db) : IRetentionTarget
{
    public string DataClass => RetentionClasses.WorkflowNotices;

    public bool CanDispose => true;

    public Task<int> CountEligibleAsync(RetentionContext context, CancellationToken ct = default)
    {
        var cutoff = context.CutoffFor(DataClass);
        return db.Notices.AsNoTracking().CountAsync(n => n.CreatedAt < cutoff, ct);
    }

    public async Task<int> DisposeAsync(RetentionContext context, int batch, CancellationToken ct = default)
    {
        if (context.TenantHeld) return 0;
        var cutoff = context.CutoffFor(DataClass);
        var ids = await db.Notices.Where(n => n.CreatedAt < cutoff).OrderBy(n => n.CreatedAt).Select(n => n.Id).Take(batch).ToListAsync(ct);
        return ids.Count == 0 ? 0 : await db.Notices.Where(n => ids.Contains(n.Id)).ExecuteDeleteAsync(ct);
    }
}
