using Microsoft.EntityFrameworkCore;
using Vpms.Workflow.Contracts;

namespace Vpms.Workflow.Infrastructure;

public sealed class ApprovalReportSource(WorkflowDbContext db) : IApprovalReportSource
{
    public async Task<IReadOnlyList<ApprovalTaskRow>> TasksAsync(Guid? legalEntityId, IReadOnlySet<Guid>? entityScope, DateOnly from, DateOnly to, int max, CancellationToken ct = default)
    {
        var start = new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var end = new DateTimeOffset(to.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var q = from t in db.Tasks.AsNoTracking()
                join r in db.Runs.AsNoTracking() on t.RunId equals r.Id
                where t.CreatedAt >= start && t.CreatedAt < end
                select new { Task = t, Run = r };
        if (legalEntityId is { } e) q = q.Where(x => x.Run.LegalEntityId == e);
        if (entityScope is not null) q = q.Where(x => entityScope.Contains(x.Run.LegalEntityId));

        var rows = await q.OrderByDescending(x => x.Task.CreatedAt).Take(Math.Clamp(max, 1, 20_000))
            .Select(x => new { x.Task.Id, x.Task.RunId, x.Run.LegalEntityId, x.Run.SubjectType, x.Run.SubjectLabel, x.Task.StepId, x.Task.AssigneeList, x.Task.Status, RunStatus = x.Run.Status,
                x.Task.CreatedAt, x.Task.DueAt, x.Task.CompletedAt, x.Task.EscalatedAt }).ToListAsync(ct);

        var ids = rows.Select(r => r.Id).ToList();
        var deciders = (await db.Decisions.AsNoTracking().Where(d => ids.Contains(d.TaskId)).OrderBy(d => d.DecidedAt).Select(d => new { d.TaskId, d.ActorId }).ToListAsync(ct))
            .GroupBy(d => d.TaskId).ToDictionary(g => g.Key, g => g.Last().ActorId);

        return rows.Select(r => new ApprovalTaskRow(
            r.Id, r.RunId, r.LegalEntityId, r.SubjectType, r.SubjectLabel, r.StepId, r.AssigneeList.Length == 0 ? [] : r.AssigneeList.Split(',').Select(Guid.Parse).ToList(), r.Status, r.RunStatus,
            r.CreatedAt, r.DueAt, r.CompletedAt, r.EscalatedAt, deciders.TryGetValue(r.Id, out var d) ? d : null)).ToList();
    }
}
