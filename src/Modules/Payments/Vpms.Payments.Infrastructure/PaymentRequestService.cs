using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vpms.BuildingBlocks.Audit;
using Vpms.BuildingBlocks.Errors;
using Vpms.BuildingBlocks.Security;
using Vpms.Invoices.Contracts;
using Vpms.Payments.Application;
using Vpms.Payments.Domain;
using Vpms.Vendors.Contracts;
using Vpms.Workflow.Contracts;

namespace Vpms.Payments.Infrastructure;

public sealed class PaymentRequestService(
    PaymentsDbContext db, IPayableInvoiceLookup invoices, IVendorLookup vendors, IBankDetailsLookup bank, IApprovalHistory approvals,
    IAuditPort audit, ICurrentUser current, TimeProvider clock) : IPaymentRequestService
{
    public async Task<IReadOnlyList<PayableInvoiceDto>> ListPayableAsync(Guid? legalEntityId, IReadOnlySet<Guid>? entityScope, CancellationToken ct = default)
    {
        var payable = await invoices.ListPayableAsync(legalEntityId, entityScope, 200, ct);
        var ids = payable.Select(p => p.Id).ToList();
        var claimed = (await db.Requests.AsNoTracking().Where(r => ids.Contains(r.InvoiceId) && RequestStatus.Open.Contains(r.Status)).Select(r => r.InvoiceId).ToListAsync(ct)).ToHashSet();
        var names = await VendorNames(payable.Select(p => p.VendorId), ct);

        var result = new List<PayableInvoiceDto>();
        foreach (var p in payable)
        {
            var open = claimed.Contains(p.Id);
            var blockers = open ? [] : await BlockersAsync(p, ct);
            result.Add(new PayableInvoiceDto(p.Id, p.LegalEntityId, p.VendorId, names.GetValueOrDefault(p.VendorId, ""), p.InvoiceNumber, p.InvoiceDate, p.DueDate ?? p.InvoiceDate,
                p.CurrencyCode, p.Total, p.ApprovedAt, open, blockers));
        }

        return result;
    }

    public async Task<IReadOnlyList<PaymentRequestDto>> ListAsync(PaymentRequestFilter f, IReadOnlySet<Guid>? entityScope, int skip, int take, CancellationToken ct = default)
    {
        var q = db.Requests.AsNoTracking().AsQueryable();
        if (entityScope is not null) q = q.Where(r => entityScope.Contains(r.LegalEntityId));
        if (!string.IsNullOrWhiteSpace(f.Status)) q = q.Where(r => r.Status == f.Status);
        if (f.LegalEntityId is { } e) q = q.Where(r => r.LegalEntityId == e);
        if (f.VendorId is { } v) q = q.Where(r => r.VendorId == v);
        var rows = await q.OrderByDescending(r => r.CreatedAt).ThenBy(r => r.Id).Skip(Math.Max(0, skip)).Take(Math.Clamp(take, 1, 200)).ToListAsync(ct);
        var names = await VendorNames(rows.Select(r => r.VendorId), ct);
        return rows.Select(r => Map(r, names)).ToList();
    }

    public async Task<IReadOnlyList<RequestResult>> CreateAsync(IReadOnlyList<Guid> invoiceIds, CancellationToken ct = default)
    {
        var results = new List<RequestResult>();
        foreach (var id in invoiceIds.Distinct().Take(200))
        {
            try
            {
                results.Add(new RequestResult(id, await CreateOneAsync(id, ct), null));
            }
            catch (ConflictException ex)
            {
                results.Add(new RequestResult(id, null, ex.Code));
            }
            catch (ValidationFailedException ex)
            {
                results.Add(new RequestResult(id, null, ex.Code));
            }
            catch (NotFoundException)
            {
                results.Add(new RequestResult(id, null, "invoice_not_found"));
            }
            catch (DbUpdateException)
            {
                db.ChangeTracker.Clear();
                results.Add(new RequestResult(id, null, "request_exists"));
            }
        }

        return results;
    }

    public async Task<PaymentRequestDto> CancelAsync(Guid requestId, string reason, CancellationToken ct = default)
    {
        var r = await db.Requests.FirstOrDefaultAsync(x => x.Id == requestId, ct) ?? throw new NotFoundException("Payment request");
        r.Cancel(reason, clock.GetUtcNow());
        audit.Record(new AuditEntry("payment.request.cancelled", "PaymentRequest", requestId.ToString(), r.LegalEntityId, JsonSerializer.Serialize(new { r.InvoiceId, reason })));
        await db.SaveChangesAsync(ct);
        return Map(r, await VendorNames([r.VendorId], ct));
    }

    public async Task<Guid> EntityOfAsync(Guid requestId, CancellationToken ct = default) =>
        await db.Requests.AsNoTracking().Where(r => r.Id == requestId).Select(r => (Guid?)r.LegalEntityId).FirstOrDefaultAsync(ct) ?? throw new NotFoundException("Payment request");

    public async Task<Guid> EntityOfInvoiceAsync(Guid invoiceId, CancellationToken ct = default) =>
        (await invoices.GetAsync(invoiceId, ct))?.LegalEntityId ?? throw new NotFoundException("Invoice");

    // --- internals -----------------------------------------------------------------------------------------

    private async Task<PaymentRequestDto> CreateOneAsync(Guid invoiceId, CancellationToken ct)
    {
        var invoice = await invoices.GetAsync(invoiceId, ct) ?? throw new NotFoundException("Invoice");
        if (!invoice.IsPayable) throw new ConflictException("invoice_not_payable", "Only an invoice that is approved for its current version can be paid.");
        if (await db.Requests.AnyAsync(r => r.InvoiceId == invoiceId && RequestStatus.Open.Contains(r.Status), ct))
            throw new ConflictException("request_exists", "This invoice already has a payment request.");
        if (!await vendors.CanTransactAsync(invoice.VendorId, invoice.LegalEntityId, ct))
            throw new ConflictException("vendor_cannot_transact", "The vendor is not active or is not linked to this legal entity.");

        var details = await bank.GetVerifiedAsync(invoice.VendorId, invoice.CurrencyCode, ct)
            ?? throw new ConflictException("no_verified_bank_details", "The vendor has no verified bank details in the invoice currency.");
        if (await bank.HasPendingChangeAsync(invoice.VendorId, ct))
            throw new ConflictException("bank_change_pending", "A bank-detail change for this vendor is waiting for a decision.");

        var people = invoice.Preparers.Concat(await approvals.GetApproversAsync("Invoice", invoiceId, ct)).Append(current.UserId);
        if (details.MadeBy is { } made) people = people.Append(made);
        if (details.CheckedBy is { } checkedBy) people = people.Append(checkedBy);

        var request = new PaymentRequest
        {
            Id = Guid.NewGuid(), LegalEntityId = invoice.LegalEntityId, VendorId = invoice.VendorId, InvoiceId = invoice.Id, InvoiceNumber = invoice.InvoiceNumber,
            InvoiceVersion = invoice.Version, Amount = invoice.Total, CurrencyCode = invoice.CurrencyCode, DueDate = invoice.DueDate ?? invoice.InvoiceDate,
            InvoiceHash = invoice.IntegrityHash, BankDetailsId = details.Id, BankFingerprint = details.Fingerprint, Involved = people.ToList(),
            CreatedBy = current.UserId, CreatedAt = clock.GetUtcNow(),
        };
        db.Requests.Add(request);
        audit.Record(new AuditEntry("payment.request.created", "PaymentRequest", request.Id.ToString(), request.LegalEntityId,
            JsonSerializer.Serialize(new { request.InvoiceId, request.Amount, request.CurrencyCode, request.DueDate, bank = details.MaskedAccount })));
        await db.SaveChangesAsync(ct);
        return Map(request, await VendorNames([request.VendorId], ct));
    }

    private async Task<IReadOnlyList<string>> BlockersAsync(PayableInvoice p, CancellationToken ct)
    {
        var blockers = new List<string>();
        if (!await vendors.CanTransactAsync(p.VendorId, p.LegalEntityId, ct)) blockers.Add("vendor_cannot_transact");
        if (await bank.GetVerifiedAsync(p.VendorId, p.CurrencyCode, ct) is null) blockers.Add("no_verified_bank_details");
        if (await bank.HasPendingChangeAsync(p.VendorId, ct)) blockers.Add("bank_change_pending");
        return blockers;
    }

    private async Task<Dictionary<Guid, string>> VendorNames(IEnumerable<Guid> ids, CancellationToken ct)
    {
        var result = new Dictionary<Guid, string>();
        foreach (var id in ids.Distinct())
        {
            var v = await vendors.FindAsync(id, ct);
            if (v is not null) result[id] = v.LegalName;
        }

        return result;
    }

    private static PaymentRequestDto Map(PaymentRequest r, IReadOnlyDictionary<Guid, string> names) =>
        new(r.Id, r.LegalEntityId, r.VendorId, names.GetValueOrDefault(r.VendorId, ""), r.InvoiceId, r.InvoiceNumber, r.InvoiceVersion, r.Amount, r.CurrencyCode, r.DueDate, r.Status,
            r.StatusReason, r.ProposalId, r.CreatedBy, r.CreatedAt);
}
