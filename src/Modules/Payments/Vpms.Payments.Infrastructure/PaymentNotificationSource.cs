using Microsoft.EntityFrameworkCore;
using Vpms.Identity.Contracts;
using Vpms.Notifications.Contracts;
using Vpms.Payments.Domain;

namespace Vpms.Payments.Infrastructure;

/// <summary>A proposal was submitted and needs treasury approval; a proposal was released (its maker and approver are told).</summary>
public sealed class PaymentNotificationSource(PaymentsDbContext db) : INotificationSource
{
    public string Name => "payments";

    public async Task<IReadOnlyList<NotificationEvent>> PollAsync(DateTimeOffset since, CancellationToken ct = default)
    {
        var events = new List<NotificationEvent>();
        var submitted = await db.Proposals.AsNoTracking().Where(p => p.SubmittedAt >= since && p.Status == ProposalStatus.PendingApproval).ToListAsync(ct);
        foreach (var p in submitted)
        {
            events.Add(new NotificationEvent(NotificationKinds.PaymentAwaitingApproval, $"proposal:{p.Id:N}:submitted:{p.SubmittedAt!.Value.UtcTicks}", p.LegalEntityId, p.ProposalNo, [], Permissions.PaymentApprove,
                $"/payments/proposals/{p.Id}", new Dictionary<string, string>(), p.SubmittedAt.Value));
        }

        var released = await db.Proposals.AsNoTracking().Where(p => p.ReleasedAt >= since && p.Status == ProposalStatus.Released).ToListAsync(ct);
        foreach (var p in released)
        {
            var people = new[] { p.CreatedBy, p.ApprovedBy }.Where(x => x.HasValue).Select(x => x!.Value).Where(x => x != p.ReleasedBy).Distinct().ToList();
            if (people.Count == 0) continue;
            events.Add(new NotificationEvent(NotificationKinds.PaymentReleased, $"proposal:{p.Id:N}:released", p.LegalEntityId, p.ProposalNo, people, null, $"/payments/proposals/{p.Id}",
                new Dictionary<string, string>(), p.ReleasedAt!.Value));
        }

        return events;
    }
}
