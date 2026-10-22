using Microsoft.EntityFrameworkCore;
using Vpms.Notifications.Contracts;

namespace Vpms.Invoices.Infrastructure;

/// <summary>Verification exceptions that were raised with a person already assigned to them.</summary>
public sealed class InvoiceNotificationSource(InvoicesDbContext db) : INotificationSource
{
    public string Name => "invoice-exceptions";

    public async Task<IReadOnlyList<NotificationEvent>> PollAsync(DateTimeOffset since, CancellationToken ct = default)
    {
        var rows = await (from x in db.Exceptions.AsNoTracking()
                          join i in db.Invoices.AsNoTracking() on x.InvoiceId equals i.Id
                          where x.CreatedAt >= since && x.AssignedTo != null && x.Status == "Open"
                          select new { x.Id, x.AssignedTo, x.RuleCode, x.LegalEntityId, x.CreatedAt, i.InvoiceNumber }).ToListAsync(ct);
        return rows.Select(x => new NotificationEvent(NotificationKinds.ExceptionAssigned, $"exception:{x.Id:N}", x.LegalEntityId, x.InvoiceNumber, [x.AssignedTo!.Value], null, "/exceptions",
            new Dictionary<string, string> { ["detail"] = x.RuleCode }, x.CreatedAt)).ToList();
    }
}
