using Microsoft.EntityFrameworkCore;
using Vpms.Payments.Contracts;
using Vpms.Payments.Domain;

namespace Vpms.Payments.Infrastructure;

public sealed class PaymentReportSource(PaymentsDbContext db) : IPaymentReportSource
{
    public async Task<IReadOnlyDictionary<Guid, string>> RequestStatusByInvoiceAsync(IReadOnlyCollection<Guid> invoiceIds, CancellationToken ct = default)
    {
        var result = new Dictionary<Guid, string>();
        foreach (var chunk in invoiceIds.Chunk(1000))
        {
            var rows = await db.Requests.AsNoTracking().Where(r => chunk.Contains(r.InvoiceId) && RequestStatus.Open.Contains(r.Status)).Select(r => new { r.InvoiceId, r.Status }).ToListAsync(ct);
            foreach (var r in rows) result[r.InvoiceId] = r.Status;
        }

        return result;
    }
}
