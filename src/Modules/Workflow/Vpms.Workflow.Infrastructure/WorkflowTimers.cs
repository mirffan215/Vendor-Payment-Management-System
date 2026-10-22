using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Vpms.BuildingBlocks.Audit;
using Vpms.BuildingBlocks.Errors;
using Vpms.BuildingBlocks.Security;
using Vpms.BuildingBlocks.Tenancy;
using Vpms.Workflow.Application;
using Vpms.Workflow.Domain;

namespace Vpms.Workflow.Infrastructure;

/// <summary>Deadlines for approval tasks: one reminder, then escalation. Each happens at most once per task, so replays and overlapping runs are harmless.</summary>
public sealed class WorkflowTimers(
    WorkflowDbContext db, WorkflowDefinitionService definitions, ApproverResolverFactory resolvers, TenantContext tenant, IAuditPort audit,
    ICurrentUser current, TimeProvider clock, ILogger<WorkflowTimers> logger) : IWorkflowTimers
{
    public async Task<TimerRunResult> RunDueAsync(CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var reminders = 0;
        var escalations = 0;

        var reminderIds = await db.Tasks.AsNoTracking().Where(t => t.Status == Domain.TaskStatus.Pending && t.ReminderAt <= now && t.ReminderSentAt == null).Select(t => t.Id).ToListAsync(ct);
        foreach (var id in reminderIds)
        {
            if (await TrySaveAsync(() => RemindAsync(id, now, ct), ct)) reminders++;
        }

        var dueIds = await db.Tasks.AsNoTracking().Where(t => t.Status == Domain.TaskStatus.Pending && t.DueAt <= now && t.EscalatedAt == null).Select(t => t.Id).ToListAsync(ct);
        foreach (var id in dueIds)
        {
            if (await TrySaveAsync(() => EscalateAsync(id, now, ct), ct)) escalations++;
        }

        return new TimerRunResult(reminders, escalations);
    }

    public async Task<IReadOnlyList<Guid>> TenantsWithDueWorkAsync(CancellationToken ct = default)
    {
        using var _ = tenant.BeginPlatformScope();
        var now = clock.GetUtcNow();
        return await db.Tasks.AsNoTracking()
            .Where(t => t.Status == Domain.TaskStatus.Pending && ((t.ReminderAt <= now && t.ReminderSentAt == null) || (t.DueAt <= now && t.EscalatedAt == null)))
            .Select(t => t.TenantId).Distinct().ToListAsync(ct);
    }

    public async Task<IReadOnlyList<NoticeDto>> NoticesAsync(bool unreadOnly, CancellationToken ct = default)
    {
        var me = current.UserId;
        var q = db.Notices.AsNoTracking().Where(n => n.UserId == me);
        if (unreadOnly) q = q.Where(n => n.ReadAt == null);
        return (await q.OrderByDescending(n => n.CreatedAt).Take(100).ToListAsync(ct)).Select(n => new NoticeDto(n.Id, n.Kind, n.RunId, n.TaskId, n.SubjectLabel, n.CreatedAt, n.ReadAt)).ToList();
    }

    public async Task MarkNoticeReadAsync(Guid id, CancellationToken ct = default)
    {
        var me = current.UserId;
        var n = await db.Notices.FirstOrDefaultAsync(x => x.Id == id && x.UserId == me, ct) ?? throw new NotFoundException("Notice");
        n.ReadAt ??= clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
    }

    // --- internals -----------------------------------------------------------------------------------------

    private async Task RemindAsync(Guid taskId, DateTimeOffset now, CancellationToken ct)
    {
        var task = await db.Tasks.FirstAsync(t => t.Id == taskId, ct);
        var run = await db.Runs.AsNoTracking().FirstAsync(r => r.Id == task.RunId, ct);
        if (run.Status != RunStatus.Active) return;

        var decided = await db.Decisions.AsNoTracking().Where(d => d.TaskId == taskId).Select(d => new[] { d.ActorId, d.ActingForId ?? d.ActorId }).ToListAsync(ct);
        var already = decided.SelectMany(x => x).ToHashSet();
        foreach (var user in task.Assignees.Where(u => !already.Contains(u)))
        {
            db.Notices.Add(Notice(user, NoticeKind.Reminder, run, task, now));
        }

        task.ReminderSentAt = now;
        audit.Record(new AuditEntry("approval.reminder-sent", "WorkflowRun", run.Id.ToString(), run.LegalEntityId, JsonSerializer.Serialize(new { taskId, step = task.StepId })));
        await db.SaveChangesAsync(ct);
    }

    private async Task EscalateAsync(Guid taskId, DateTimeOffset now, CancellationToken ct)
    {
        var runId = await db.Tasks.AsNoTracking().Where(t => t.Id == taskId).Select(t => t.RunId).FirstAsync(ct);
        var run = await db.Runs.Include(r => r.Tasks).ThenInclude(t => t.Decisions).Include(r => r.Joins).FirstAsync(r => r.Id == runId, ct);
        if (run.Status != RunStatus.Active) return;

        var definition = await definitions.GetVersionAsync(run.DefinitionId, ct) ?? throw new NotFoundException("Workflow version");
        var resolver = await resolvers.CreateAsync(run.LegalEntityId, definition.Spec, ct);
        var task = run.Tasks.First(t => t.Id == taskId);
        var before = task.Assignees.ToList();
        var added = run.Escalate(definition.Spec, taskId, resolver, now);

        foreach (var user in added) db.Notices.Add(Notice(user, NoticeKind.Escalated, run, task, now));
        foreach (var user in before.Where(u => !task.Decisions.Any(d => d.ActorId == u || d.ActingForId == u))) db.Notices.Add(Notice(user, NoticeKind.EscalatedAway, run, task, now));

        var step = definition.Spec.Find(task.StepId)!;
        var action = step.EscalateTo is null ? "approval.overdue" : added.Count > 0 ? "approval.escalated" : "approval.escalation-failed";
        audit.Record(new AuditEntry(action, "WorkflowRun", run.Id.ToString(), run.LegalEntityId,
            JsonSerializer.Serialize(new { taskId, step = task.StepId, escalatedTo = added })));
        await db.SaveChangesAsync(ct);
    }

    private static WorkflowNotice Notice(Guid user, string kind, WorkflowRun run, ApprovalTask task, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(), UserId = user, Kind = kind, RunId = run.Id, TaskId = task.Id, SubjectLabel = run.SubjectLabel, CreatedAt = now,
    };

    /// <summary>One task failing (for example a concurrent decision) never stops the rest; it is picked up again on the next run.</summary>
    private async Task<bool> TrySaveAsync(Func<Task> work, CancellationToken ct)
    {
        try
        {
            await work();
            return true;
        }
        catch (Exception ex) when (ex is DbUpdateException or ConflictException && !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "A workflow timer step was skipped and will be retried.");
            db.ChangeTracker.Clear();
            return false;
        }
    }
}
