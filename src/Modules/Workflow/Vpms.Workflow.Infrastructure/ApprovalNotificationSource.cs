using Microsoft.EntityFrameworkCore;
using Vpms.Identity.Contracts;
using Vpms.Notifications.Contracts;
using Vpms.Workflow.Domain;

namespace Vpms.Workflow.Infrastructure;

/// <summary>Approval tasks that arrived for someone, and the reminders and escalations the timers raised.</summary>
public sealed class ApprovalNotificationSource(WorkflowDbContext db) : INotificationSource
{
    public string Name => "approvals";

    public async Task<IReadOnlyList<NotificationEvent>> PollAsync(DateTimeOffset since, CancellationToken ct = default)
    {
        var events = new List<NotificationEvent>();

        var tasks = await (from t in db.Tasks.AsNoTracking()
                           join r in db.Runs.AsNoTracking() on t.RunId equals r.Id
                           where t.CreatedAt >= since
                           select new { t.Id, t.AssigneeList, t.StepId, t.DueAt, t.CreatedAt, r.SubjectLabel, r.LegalEntityId }).ToListAsync(ct);
        foreach (var t in tasks.Where(t => t.AssigneeList.Length > 0))
        {
            events.Add(new NotificationEvent(NotificationKinds.ApprovalAssigned, $"task:{t.Id:N}", t.LegalEntityId, t.SubjectLabel, t.AssigneeList.Split(',').Select(Guid.Parse).ToList(), null,
                $"/approvals/{t.Id}", new Dictionary<string, string> { ["detail"] = t.StepId }, t.CreatedAt));
        }

        var notices = await (from n in db.Notices.AsNoTracking()
                             join r in db.Runs.AsNoTracking() on n.RunId equals r.Id
                             where n.CreatedAt >= since
                             select new { n.Id, n.UserId, n.Kind, n.TaskId, n.SubjectLabel, n.CreatedAt, r.LegalEntityId }).ToListAsync(ct);
        foreach (var n in notices)
        {
            var kind = n.Kind switch
            {
                NoticeKind.Reminder => NotificationKinds.ApprovalReminder,
                NoticeKind.Escalated or NoticeKind.EscalatedAway => NotificationKinds.ApprovalEscalated,
                _ => null,
            };
            if (kind is null) continue;
            events.Add(new NotificationEvent(kind, $"notice:{n.Id:N}", n.LegalEntityId, n.SubjectLabel, [n.UserId], null, $"/approvals/{n.TaskId}", new Dictionary<string, string>(), n.CreatedAt));
        }

        return events;
    }
}
