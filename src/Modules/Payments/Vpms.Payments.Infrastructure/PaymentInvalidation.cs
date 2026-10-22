using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vpms.BuildingBlocks.Audit;
using Vpms.Payments.Domain;

namespace Vpms.Payments.Infrastructure;

/// <summary>
/// The one place that voids payments whose basis changed. A request whose invoice, vendor or bank details changed is Invalidated; the
/// proposal that holds it is Invalidated with it, and the proposal's other requests become available again. Released payments are
/// never touched.
/// </summary>
public sealed class PaymentInvalidation(PaymentsDbContext db, IAuditPort audit, TimeProvider clock)
{
    /// <summary>Does not save. Returns true when anything changed.</summary>
    public async Task<bool> InvalidateRequestAsync(PaymentRequest request, string reason, CancellationToken ct)
    {
        if (request.Status is not (RequestStatus.Created or RequestStatus.InProposal)) return false;
        var now = clock.GetUtcNow();
        var proposalId = request.ProposalId;
        request.Invalidate(reason, now);
        audit.Record(new AuditEntry("payment.request.invalidated", "PaymentRequest", request.Id.ToString(), request.LegalEntityId,
            JsonSerializer.Serialize(new { request.InvoiceId, reason })));

        if (proposalId is { } pid)
        {
            var proposal = await db.Proposals.Include(p => p.Items).FirstOrDefaultAsync(p => p.Id == pid, ct);
            if (proposal is not null && ProposalStatus.Live.Contains(proposal.Status)) await InvalidateProposalCoreAsync(proposal, reason, now, ct);
        }

        return true;
    }

    /// <summary>Does not save.</summary>
    public async Task InvalidateProposalAsync(PaymentProposal proposal, string reason, CancellationToken ct) =>
        await InvalidateProposalCoreAsync(proposal, reason, clock.GetUtcNow(), ct);

    private async Task InvalidateProposalCoreAsync(PaymentProposal proposal, string reason, DateTimeOffset now, CancellationToken ct)
    {
        proposal.Invalidate(reason, now);
        var ids = proposal.Items.Select(i => i.PaymentRequestId).ToList();
        foreach (var other in await db.Requests.Where(r => ids.Contains(r.Id)).ToListAsync(ct)) other.ReturnToPool();
        audit.Record(new AuditEntry("payment.proposal.invalidated", "PaymentProposal", proposal.Id.ToString(), proposal.LegalEntityId,
            JsonSerializer.Serialize(new { proposal.ProposalNo, reason })));
    }
}
