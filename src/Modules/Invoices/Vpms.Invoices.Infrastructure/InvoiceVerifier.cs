using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vpms.BuildingBlocks.Audit;
using Vpms.BuildingBlocks.Errors;
using Vpms.BuildingBlocks.Tenancy;
using Vpms.Invoices.Application;
using Vpms.Invoices.Domain;
using Vpms.Invoices.Domain.Verification;
using Vpms.Vendors.Contracts;

namespace Vpms.Invoices.Infrastructure;

/// <summary>Gathers what the rules need, runs the pure engine and records the outcome, exceptions and new invoice status in one transaction.</summary>
public sealed class InvoiceVerifier(
    InvoicesDbContext db, IVerificationRuleSetService ruleSets, IVendorLookup vendors, IAuditPort audit, TenantContext tenant, TimeProvider clock,
    VerificationEngine? engine = null) : IInvoiceVerifier, IVerificationScan
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly string[] LiveStatuses =
        [InvoiceStatus.Received, InvoiceStatus.InVerification, InvoiceStatus.Exception, InvoiceStatus.Verified, InvoiceStatus.ReadyForApproval];

    private readonly VerificationEngine _engine = engine ?? VerificationEngine.Standard();

    public async Task<VerificationOutcome> VerifyAsync(Guid invoiceId, CancellationToken ct = default)
    {
        var invoice = await db.Invoices.Include(i => i.Lines).FirstOrDefaultAsync(i => i.Id == invoiceId, ct) ?? throw new NotFoundException("Invoice");
        if (invoice.Status != InvoiceStatus.Received) return await CurrentOutcome(invoice, ct); // nothing waiting: calling again changes nothing

        var (ruleSetVersion, config) = await ruleSets.ActiveConfigAsync(invoice.LegalEntityId, ct);
        var now = clock.GetUtcNow();
        var input = await BuildInputAsync(invoice, config, now, ct);
        var results = _engine.Run(input, config);

        invoice.StartVerification();
        var runId = Guid.NewGuid();
        foreach (var r in results)
        {
            db.VerificationResults.Add(new VerificationResult
            {
                Id = Guid.NewGuid(), LegalEntityId = invoice.LegalEntityId, VendorId = invoice.VendorId, InvoiceId = invoice.Id, RunId = runId,
                InvoiceVersion = invoice.Version, RuleSetVersion = ruleSetVersion, RuleCode = r.RuleCode, Outcome = r.Outcome, Severity = r.Severity,
                DetailJson = JsonSerializer.Serialize(r.Findings.Select(f => new FindingDto(f.Key, f.Evidence)), Json), CreatedAt = now,
            });
        }

        var existing = await db.Exceptions.Where(e => e.InvoiceId == invoice.Id).ToListAsync(ct);
        foreach (var r in results)
        {
            var open = existing.FirstOrDefault(e => e.RuleCode == r.RuleCode && e.Status == ExceptionStatus.Open);
            if (!r.Failed)
            {
                open?.ResolveByVerification(now);
                continue;
            }

            if (existing.Any(e => e.RuleCode == r.RuleCode && e.Status == ExceptionStatus.Waived && e.InvoiceVersion == invoice.Version)) continue; // accepted by a person

            var detail = JsonSerializer.Serialize(r.Findings.Select(f => new FindingDto(f.Key, f.Evidence)), Json);
            if (open is not null)
            {
                open.Severity = r.Severity;
                open.DetailJson = detail;
                open.InvoiceVersion = invoice.Version;
            }
            else
            {
                db.Exceptions.Add(new InvoiceException
                {
                    Id = Guid.NewGuid(), LegalEntityId = invoice.LegalEntityId, VendorId = invoice.VendorId, InvoiceId = invoice.Id, RuleCode = r.RuleCode,
                    Severity = r.Severity, DetailJson = detail, InvoiceVersion = invoice.Version, CreatedAt = now,
                });
            }
        }

        var failed = results.Where(r => r.Failed).Select(r => r.RuleCode).ToList();
        var stillFailing = results.Where(r => r.Failed && !existing.Any(e => e.RuleCode == r.RuleCode && e.Status == ExceptionStatus.Waived && e.InvoiceVersion == invoice.Version)).Select(r => r.RuleCode).ToList();
        if (stillFailing.Count == 0) invoice.MarkVerified();
        else invoice.MarkException(string.Join(',', stillFailing));

        audit.Record(new AuditEntry("invoice.verified", "Invoice", invoice.Id.ToString(), invoice.LegalEntityId, ActorType: AuditActorType.System,
            ChangeSummaryJson: JsonSerializer.Serialize(new { status = invoice.Status, version = invoice.Version, ruleSetVersion, failed, open = stillFailing })));
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Someone else verified or edited the invoice at the same moment; their result stands.
            db.ChangeTracker.Clear();
            return await CurrentOutcome(await db.Invoices.AsNoTracking().FirstAsync(i => i.Id == invoiceId, ct), ct);
        }

        return new VerificationOutcome(invoice.Status, failed.Count, stillFailing.Count, ruleSetVersion);
    }

    private async Task<VerificationOutcome> CurrentOutcome(Invoice invoice, CancellationToken ct)
    {
        var open = await db.Exceptions.AsNoTracking().CountAsync(e => e.InvoiceId == invoice.Id && e.Status == ExceptionStatus.Open, ct);
        var version = await db.VerificationResults.AsNoTracking().Where(r => r.InvoiceId == invoice.Id).OrderByDescending(r => r.CreatedAt).Select(r => (int?)r.RuleSetVersion).FirstOrDefaultAsync(ct) ?? 0;
        return new VerificationOutcome(invoice.Status, open, open, version);
    }

    private async Task<VerificationInput> BuildInputAsync(Invoice invoice, RuleConfig config, DateTimeOffset now, CancellationToken ct)
    {
        var facts = new InvoiceFacts(invoice.Id, invoice.VendorId, invoice.LegalEntityId, invoice.InvoiceNumber, invoice.InvoiceDate, invoice.DueDate, invoice.CurrencyCode,
            invoice.Subtotal, invoice.TaxTotal, invoice.Total, invoice.PoReference, invoice.SupplierTaxInvoiceRef,
            invoice.Lines.OrderBy(l => l.LineNo).Select(l => new LineFacts(l.LineNo, l.Description, l.Quantity, l.UnitPrice, l.LineAmount, l.TaxCode, l.TaxRate, l.TaxAmount, l.PoLineRef)).ToList());

        var canTransact = await vendors.CanTransactAsync(invoice.VendorId, invoice.LegalEntityId, ct);
        return new VerificationInput(facts, new VendorFacts(canTransact), await CandidatesAsync(invoice, config, ct), await PurchaseOrderAsync(invoice, ct),
            DateOnly.FromDateTime(now.UtcDateTime));
    }

    /// <summary>Other live invoices of the same vendor in the same legal entity: the same number, or close in date.</summary>
    private async Task<IReadOnlyList<CandidateFacts>> CandidatesAsync(Invoice invoice, RuleConfig config, CancellationToken ct)
    {
        var window = (int)config.Param(RuleCodes.DuplicateFuzzy, "dateWindowDays");
        var from = invoice.InvoiceDate.AddDays(-window);
        var to = invoice.InvoiceDate.AddDays(window);
        var rows = await db.Invoices.AsNoTracking()
            .Where(i => i.Id != invoice.Id && i.LegalEntityId == invoice.LegalEntityId && i.VendorId == invoice.VendorId && LiveStatuses.Contains(i.Status)
                        && ((invoice.NumberKeyIndex != null && i.NumberKeyIndex == invoice.NumberKeyIndex) || (i.InvoiceDate >= from && i.InvoiceDate <= to)))
            .OrderBy(i => i.CreatedAt).Take(500).ToListAsync(ct);
        return rows.Select(i => new CandidateFacts(i.Id, i.InvoiceNumber, i.InvoiceDate, i.Total, i.CurrencyCode, i.Status)).ToList();
    }

    private async Task<PoFacts?> PurchaseOrderAsync(Invoice invoice, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(invoice.PoReference)) return null;
        var number = invoice.PoReference.Trim();
        var po = await db.PurchaseOrders.AsNoTracking().Include(p => p.Lines).FirstOrDefaultAsync(p => p.LegalEntityId == invoice.LegalEntityId && p.PoNumber == number, ct);
        if (po is null) return null;

        // What other live invoices of the entity have already used of this order.
        var others = await db.Invoices.AsNoTracking().Include(i => i.Lines)
            .Where(i => i.Id != invoice.Id && i.LegalEntityId == invoice.LegalEntityId && i.PoReference == number && LiveStatuses.Contains(i.Status)).ToListAsync(ct);
        var usedByLine = others.SelectMany(i => i.Lines).Where(l => l.PoLineRef != null).GroupBy(l => l.PoLineRef!.Trim()).ToDictionary(g => g.Key, g => g.Sum(l => l.Quantity));

        return new PoFacts(po.PoNumber, po.VendorId, po.CurrencyCode, po.Status,
            po.Lines.OrderBy(l => l.LineNo).Select(l => new PoLineFacts(l.LineNo, l.OrderedQuantity, l.UnitPrice, l.PreviouslyInvoicedQuantity + usedByLine.GetValueOrDefault(l.LineNo))).ToList(),
            po.PreviouslyInvoicedValue + others.Sum(i => i.Subtotal));
    }

    // --- worker safety net ---------------------------------------------------------------------------------

    public async Task<IReadOnlyList<Guid>> TenantsWithUnverifiedInvoicesAsync(TimeSpan olderThan, CancellationToken ct = default)
    {
        using var _ = tenant.BeginPlatformScope();
        var cutoff = clock.GetUtcNow() - olderThan;
        return await db.Invoices.AsNoTracking().Where(i => i.Status == InvoiceStatus.Received && i.ReceivedAt != null && i.ReceivedAt <= cutoff)
            .Select(i => i.TenantId).Distinct().ToListAsync(ct);
    }

    public async Task<int> VerifyStaleAsync(TimeSpan olderThan, int max = 50, CancellationToken ct = default)
    {
        var cutoff = clock.GetUtcNow() - olderThan;
        var ids = await db.Invoices.AsNoTracking().Where(i => i.Status == InvoiceStatus.Received && i.ReceivedAt != null && i.ReceivedAt <= cutoff)
            .OrderBy(i => i.ReceivedAt).Select(i => i.Id).Take(max).ToListAsync(ct);
        foreach (var id in ids) await VerifyAsync(id, ct);
        return ids.Count;
    }
}
