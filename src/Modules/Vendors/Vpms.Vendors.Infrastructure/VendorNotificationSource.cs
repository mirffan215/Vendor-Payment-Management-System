using Microsoft.EntityFrameworkCore;
using Vpms.Identity.Contracts;
using Vpms.Notifications.Contracts;
using Vpms.Vendors.Domain;

namespace Vpms.Vendors.Infrastructure;

/// <summary>A bank-details change is waiting for a second person: told to everyone who may verify it for an entity the vendor serves.</summary>
public sealed class VendorNotificationSource(VendorsDbContext db) : INotificationSource
{
    public string Name => "bank-changes";

    public async Task<IReadOnlyList<NotificationEvent>> PollAsync(DateTimeOffset since, CancellationToken ct = default)
    {
        var changes = await (from c in db.BankDetailChanges.AsNoTracking()
                             join v in db.Vendors.AsNoTracking() on c.VendorId equals v.Id
                             where c.MadeAt >= since && c.Status == ChangeStatus.Pending
                             select new { c.Id, c.VendorId, c.MadeAt, v.LegalName }).ToListAsync(ct);
        var events = new List<NotificationEvent>();
        foreach (var c in changes)
        {
            var entities = await db.EntityLinks.AsNoTracking().Where(l => l.VendorId == c.VendorId && l.Status == "Active").Select(l => l.LegalEntityId).ToListAsync(ct);
            foreach (var entity in entities)
            {
                events.Add(new NotificationEvent(NotificationKinds.BankChangePending, $"bankchange:{c.Id:N}:{entity:N}", entity, c.LegalName, [], Permissions.VendorBankDetailsApprove, "/bank-changes",
                    new Dictionary<string, string>(), c.MadeAt));
            }
        }

        return events;
    }
}
