using Microsoft.EntityFrameworkCore;
using Vpms.Payments.Contracts;
using Vpms.Payments.Domain;

namespace Vpms.Payments.Infrastructure;

public sealed class PaymentLifecycleService(PaymentsDbContext db, PaymentInvalidation invalidation) : IPaymentLifecycle
{
    public async Task InvoiceChangedAsync(Guid invoiceId, string reason, CancellationToken ct = default)
    {
        var request = await db.Requests.FirstOrDefaultAsync(r => r.InvoiceId == invoiceId && (r.Status == RequestStatus.Created || r.Status == RequestStatus.InProposal), ct);
        if (request is null) return;
        if (await invalidation.InvalidateRequestAsync(request, reason, ct)) await db.SaveChangesAsync(ct);
    }

    public Task<bool> HasReleasedPaymentAsync(Guid invoiceId, CancellationToken ct = default) =>
        db.Requests.AnyAsync(r => r.InvoiceId == invoiceId && r.Status == RequestStatus.Released, ct);
}
