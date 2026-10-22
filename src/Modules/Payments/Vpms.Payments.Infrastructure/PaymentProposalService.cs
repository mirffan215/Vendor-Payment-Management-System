using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vpms.BuildingBlocks.Audit;
using Vpms.BuildingBlocks.Errors;
using Vpms.BuildingBlocks.Security;
using Vpms.BuildingBlocks.Tenancy;
using Vpms.Payments.Application;
using Vpms.Payments.Domain;
using Vpms.Vendors.Contracts;

namespace Vpms.Payments.Infrastructure;

public sealed class PaymentProposalService(
    PaymentsDbContext db, PaymentRechecker rechecker, PaymentInvalidation invalidation, IVendorLookup vendors, TenantContext tenant,
    IAuditPort audit, ICurrentUser current, TimeProvider clock) : IPaymentProposalService, IPaymentScan
{
    public async Task<IReadOnlyList<ProposalDto>> ListAsync(ProposalFilter f, IReadOnlySet<Guid>? entityScope, int skip, int take, CancellationToken ct = default)
    {
        var q = db.Proposals.AsNoTracking().Include(p => p.Items).AsQueryable();
        if (entityScope is not null) q = q.Where(p => entityScope.Contains(p.LegalEntityId));
        if (!string.IsNullOrWhiteSpace(f.Status)) q = q.Where(p => p.Status == f.Status);
        if (f.LegalEntityId is { } e) q = q.Where(p => p.LegalEntityId == e);
        var rows = await q.OrderByDescending(p => p.CreatedAt).ThenBy(p => p.Id).Skip(Math.Max(0, skip)).Take(Math.Clamp(take, 1, 200)).ToListAsync(ct);
        return await MapAsync(rows, ct);
    }

    public async Task<ProposalDto> GetAsync(Guid id, CancellationToken ct = default) =>
        (await MapAsync([await Load(id, track: false, ct)], ct)).Single();

    public async Task<Guid> EntityOfAsync(Guid id, CancellationToken ct = default) =>
        await db.Proposals.AsNoTracking().Where(p => p.Id == id).Select(p => (Guid?)p.LegalEntityId).FirstOrDefaultAsync(ct) ?? throw new NotFoundException("Payment proposal");

    public async Task<IReadOnlyList<ProposalDto>> CreateAsync(CreateProposals r, CancellationToken ct = default)
    {
        var q = db.Requests.Where(x => x.LegalEntityId == r.LegalEntityId && x.Status == RequestStatus.Created);
        if (r.DueOnOrBefore is { } due) q = q.Where(x => x.DueDate <= due);
        if (!string.IsNullOrWhiteSpace(r.CurrencyCode)) q = q.Where(x => x.CurrencyCode == r.CurrencyCode);
        if (r.VendorId is { } v) q = q.Where(x => x.VendorId == v);
        if (r.RequestIds is { Count: > 0 } ids) q = q.Where(x => ids.Contains(x.Id));
        var available = await q.ToListAsync(ct);
        if (r.RequestIds is { Count: > 0 } wanted && available.Count != wanted.Distinct().Count())
        {
            throw new ConflictException("request_not_available", "Some of the chosen requests do not exist, belong to another legal entity, or are already in a proposal.");
        }

        var created = new List<PaymentProposal>();
        foreach (var (key, requests) in ProposalGrouping.Group(available))
        {
            created.Add(await CreateOneAsync(key, requests, ct));
        }

        return await MapAsync(created, ct);
    }

    public async Task<ProposalDto> RemoveRequestAsync(Guid proposalId, Guid paymentRequestId, CancellationToken ct = default)
    {
        var p = await Load(proposalId, track: true, ct);
        if (p.Status != ProposalStatus.Draft) throw new ConflictException("invalid_transition", "Requests can only be taken out of a draft proposal.");
        var item = p.Items.FirstOrDefault(i => i.PaymentRequestId == paymentRequestId) ?? throw new NotFoundException("Payment request in proposal");
        var request = await db.Requests.FirstAsync(x => x.Id == paymentRequestId, ct);

        p.Items.Remove(item);
        db.Items.Remove(item);
        request.ReturnToPool();
        p.Recalculate();
        audit.Record(new AuditEntry("payment.proposal.request-removed", "PaymentProposal", p.Id.ToString(), p.LegalEntityId, JsonSerializer.Serialize(new { paymentRequestId, request.InvoiceId })));
        if (p.Items.Count == 0) p.Cancel("Every request was taken out.", clock.GetUtcNow());
        await db.SaveChangesAsync(ct);
        return (await MapAsync([p], ct)).Single();
    }

    public async Task<ProposalDto> SubmitAsync(Guid id, CancellationToken ct = default)
    {
        var p = await Load(id, track: true, ct);
        RequireStatus(p, ProposalStatus.Draft, "Only a draft proposal can be submitted for approval.");
        await EnforceRecheckAsync(p, ct);
        p.Submit(current.UserId, clock.GetUtcNow());
        audit.Record(new AuditEntry("payment.proposal.submitted", "PaymentProposal", id.ToString(), p.LegalEntityId, JsonSerializer.Serialize(new { p.ProposalNo, p.TotalAmount, p.CurrencyCode })));
        await db.SaveChangesAsync(ct);
        return (await MapAsync([p], ct)).Single();
    }

    public async Task<ProposalDto> ApproveAsync(Guid id, string? comment, CancellationToken ct = default)
    {
        var p = await Load(id, track: true, ct);
        RequireStatus(p, ProposalStatus.PendingApproval, "Only a proposal waiting for approval can be approved.");
        await EnsureNotInvolvedAsync(p, "approve", ct);
        await EnforceRecheckAsync(p, ct);
        try
        {
            p.Approve(current.UserId, comment, clock.GetUtcNow());
        }
        catch (SegregationOfDutiesException ex)
        {
            await audit.WriteNowAsync(new AuditEntry("sod.payment.blocked", "PaymentProposal", id.ToString(), p.LegalEntityId,
                JsonSerializer.Serialize(new { attempted = "approve", rule = ex.Rule })), ct);
            throw;
        }

        audit.Record(new AuditEntry("payment.proposal.approved", "PaymentProposal", id.ToString(), p.LegalEntityId, JsonSerializer.Serialize(new { p.ProposalNo, p.TotalAmount, p.CurrencyCode })));
        await db.SaveChangesAsync(ct);
        return (await MapAsync([p], ct)).Single();
    }

    public async Task<ProposalDto> SendBackAsync(Guid id, string reason, CancellationToken ct = default)
    {
        var p = await Load(id, track: true, ct);
        try
        {
            p.SendBack(current.UserId, reason, clock.GetUtcNow());
        }
        catch (SegregationOfDutiesException ex)
        {
            await audit.WriteNowAsync(new AuditEntry("sod.payment.blocked", "PaymentProposal", id.ToString(), p.LegalEntityId,
                JsonSerializer.Serialize(new { attempted = "send-back", rule = ex.Rule })), ct);
            throw;
        }

        audit.Record(new AuditEntry("payment.proposal.sent-back", "PaymentProposal", id.ToString(), p.LegalEntityId, JsonSerializer.Serialize(new { reason })));
        await db.SaveChangesAsync(ct);
        return (await MapAsync([p], ct)).Single();
    }

    public async Task<ProposalDto> CancelAsync(Guid id, string reason, CancellationToken ct = default)
    {
        var p = await Load(id, track: true, ct);
        p.Cancel(reason, clock.GetUtcNow());
        var ids = p.Items.Select(i => i.PaymentRequestId).ToList();
        foreach (var r in await db.Requests.Where(x => ids.Contains(x.Id)).ToListAsync(ct)) r.ReturnToPool();
        audit.Record(new AuditEntry("payment.proposal.cancelled", "PaymentProposal", id.ToString(), p.LegalEntityId, JsonSerializer.Serialize(new { reason })));
        await db.SaveChangesAsync(ct);
        return (await MapAsync([p], ct)).Single();
    }

    public async Task<RecheckDto> PreviewRecheckAsync(Guid id, CancellationToken ct = default)
    {
        var p = await Load(id, track: false, ct);
        var result = await rechecker.CheckAsync(await RequestsOf(p, ct), ct);
        return new RecheckDto(result.Passed, result.Invalidates, result.Findings);
    }

    public async Task<int> SweepAsync(CancellationToken ct = default)
    {
        var live = await db.Proposals.Include(p => p.Items).Where(p => p.Status == ProposalStatus.Draft || p.Status == ProposalStatus.PendingApproval || p.Status == ProposalStatus.Approved)
            .OrderBy(p => p.CreatedAt).Take(100).ToListAsync(ct);
        var invalidated = 0;
        foreach (var p in live)
        {
            var result = await rechecker.CheckAsync(await RequestsOf(p, ct), ct);
            if (!result.Invalidates) continue;
            await InvalidateFromAsync(p, result, ct);
            await db.SaveChangesAsync(ct);
            invalidated++;
        }

        return invalidated;
    }

    public async Task<IReadOnlyList<Guid>> TenantsWithOpenProposalsAsync(CancellationToken ct = default)
    {
        using var _ = tenant.BeginPlatformScope();
        return await db.Proposals.AsNoTracking().Where(p => p.Status == ProposalStatus.Draft || p.Status == ProposalStatus.PendingApproval || p.Status == ProposalStatus.Approved)
            .Select(p => p.TenantId).Distinct().ToListAsync(ct);
    }

    // --- shared with the release service -------------------------------------------------------------------

    /// <summary>
    /// Re-checks every request of the proposal. If the basis of any changed, the affected requests and the proposal are invalidated (and
    /// saved) before the exception is thrown, so the refusal leaves a record. A condition that may resolve only blocks the step.
    /// </summary>
    internal async Task EnforceRecheckAsync(PaymentProposal p, CancellationToken ct)
    {
        var result = await rechecker.CheckAsync(await RequestsOf(p, ct), ct);
        if (result.Passed) return;

        if (result.Invalidates)
        {
            await InvalidateFromAsync(p, result, ct);
            await db.SaveChangesAsync(ct);
            throw new ConflictException("recheck_failed", "Something changed since this proposal was prepared, so it was invalidated. Prepare it again.");
        }

        throw new ConflictException("recheck_blocked", "A bank-detail change for one of the vendors is waiting for a decision. Try again once it is decided.");
    }

    internal async Task InvalidateFromAsync(PaymentProposal p, RecheckResult result, CancellationToken ct)
    {
        var reason = string.Join(", ", result.Findings.Where(f => f.Severity == RecheckSeverity.Invalidates).Select(f => f.Code).Distinct());
        foreach (var requestId in result.Findings.Where(f => f.Severity == RecheckSeverity.Invalidates).Select(f => f.PaymentRequestId).Distinct())
        {
            var request = await db.Requests.FirstOrDefaultAsync(x => x.Id == requestId, ct);
            if (request is not null) await invalidation.InvalidateRequestAsync(request, reason, ct);
        }

        if (ProposalStatus.Live.Contains(p.Status)) await invalidation.InvalidateProposalAsync(p, reason, ct);
    }

    internal Task<List<PaymentRequest>> RequestsOf(PaymentProposal p, CancellationToken ct)
    {
        var ids = p.Items.Select(i => i.PaymentRequestId).ToList();
        return db.Requests.Where(r => ids.Contains(r.Id)).ToListAsync(ct);
    }

    /// <summary>The approver must not have prepared, approved or checked anything in the proposal's invoices or bank details.</summary>
    private async Task EnsureNotInvolvedAsync(PaymentProposal p, string action, CancellationToken ct)
    {
        var involved = (await RequestsOf(p, ct)).SelectMany(r => r.Involved).ToHashSet();
        if (!involved.Contains(current.UserId)) return;
        await audit.WriteNowAsync(new AuditEntry("sod.payment.blocked", "PaymentProposal", p.Id.ToString(), p.LegalEntityId,
            JsonSerializer.Serialize(new { attempted = action, rule = "involved-in-payment" })), ct);
        throw new SegregationOfDutiesException("involved-in-payment", $"'{action}' must be done by someone who took no part in these invoices, their approval or the vendor's bank details.");
    }

    // --- internals -----------------------------------------------------------------------------------------

    private async Task<PaymentProposal> CreateOneAsync(GroupingKey key, IReadOnlyList<PaymentRequest> requests, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var now = clock.GetUtcNow();
            var seq = (await db.Proposals.Where(p => p.LegalEntityId == key.LegalEntityId).MaxAsync(p => (int?)p.SequenceNo, ct) ?? 0) + 1;
            var proposal = new PaymentProposal
            {
                Id = Guid.NewGuid(), LegalEntityId = key.LegalEntityId, SequenceNo = seq, ProposalNo = PaymentProposal.FormatNo(seq, now), VendorId = key.VendorId,
                DueDate = key.DueDate, CurrencyCode = key.CurrencyCode, CreatedBy = current.UserId, CreatedAt = now,
            };
            foreach (var r in requests)
            {
                r.AddToProposal(proposal.Id);
                proposal.Items.Add(new PaymentProposalItem { Id = Guid.NewGuid(), ProposalId = proposal.Id, PaymentRequestId = r.Id, InvoiceId = r.InvoiceId, Amount = r.Amount });
            }

            proposal.Recalculate();
            db.Proposals.Add(proposal);
            audit.Record(new AuditEntry("payment.proposal.created", "PaymentProposal", proposal.Id.ToString(), proposal.LegalEntityId,
                JsonSerializer.Serialize(new { proposal.ProposalNo, proposal.VendorId, proposal.DueDate, proposal.CurrencyCode, proposal.TotalAmount, requests = requests.Count })));
            try
            {
                await db.SaveChangesAsync(ct);
                return proposal;
            }
            catch (DbUpdateException) when (attempt < 3)
            {
                // another proposal took the same number; undo this attempt and take the next one
                db.Entry(proposal).State = EntityState.Detached;
                foreach (var item in proposal.Items) db.Entry(item).State = EntityState.Detached;
                foreach (var r in requests) await db.Entry(r).ReloadAsync(ct);
            }
        }
    }

    internal static void RequireStatus(PaymentProposal p, string status, string message)
    {
        if (p.Status != status) throw new ConflictException("invalid_transition", message);
    }

    private async Task<PaymentProposal> Load(Guid id, bool track, CancellationToken ct)
    {
        var q = db.Proposals.Include(p => p.Items).AsQueryable();
        if (!track) q = q.AsNoTracking();
        return await q.FirstOrDefaultAsync(p => p.Id == id, ct) ?? throw new NotFoundException("Payment proposal");
    }

    private async Task<IReadOnlyList<ProposalDto>> MapAsync(IReadOnlyList<PaymentProposal> rows, CancellationToken ct)
    {
        var requestIds = rows.SelectMany(p => p.Items.Select(i => i.PaymentRequestId)).ToList();
        var numbers = await db.Requests.AsNoTracking().Where(r => requestIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, r => r.InvoiceNumber, ct);
        var names = new Dictionary<Guid, string>();
        foreach (var v in rows.Select(r => r.VendorId).Distinct())
        {
            var s = await vendors.FindAsync(v, ct);
            if (s is not null) names[v] = s.LegalName;
        }

        return rows.Select(p => new ProposalDto(p.Id, p.LegalEntityId, p.ProposalNo, p.VendorId, names.GetValueOrDefault(p.VendorId, ""), p.DueDate, p.CurrencyCode, p.TotalAmount,
            p.Status, p.StatusReason, p.CreatedBy, p.CreatedAt, p.SubmittedBy, p.ApprovedBy, p.ApprovedAt, p.ApprovalComment, p.ReleasedBy, p.ReleasedAt,
            p.Items.Select(i => new ProposalItemDto(i.PaymentRequestId, i.InvoiceId, numbers.GetValueOrDefault(i.PaymentRequestId, ""), i.Amount)).ToList())).ToList();
    }
}
