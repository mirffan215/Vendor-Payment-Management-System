using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vpms.BuildingBlocks.Audit;
using Vpms.BuildingBlocks.Errors;
using Vpms.BuildingBlocks.Security;
using Vpms.CustomerAdmin.Contracts;
using Vpms.ErpIntegration.Application;
using Vpms.ErpIntegration.Contracts;
using Vpms.ErpIntegration.Domain;
using Vpms.Invoices.Contracts;
using Vpms.Payments.Contracts;
using Vpms.Vendors.Contracts;

namespace Vpms.ErpIntegration.Infrastructure;

/// <summary>
/// Finds where VPMS and the ERP disagree about the invoices VPMS posted, and what the ERP holds that VPMS never posted for its vendors. It
/// only reads: nothing is changed in either system. Each difference becomes an item a person has to account for.
/// </summary>
public sealed class ReconciliationService(
    ErpDbContext db, ErpContextResolver contexts, IInvoicePostingSource invoices, IVendorErpSource vendors, IPaymentLifecycle payments, ILegalEntityLookup entities,
    IAuditPort audit, ICurrentUser current, TimeProvider clock) : IReconciliationService
{
    private const decimal Tolerance = 0.005m;

    public async Task<ReconRunDto> RunAsync(Guid legalEntityId, CancellationToken ct = default)
    {
        if (!await entities.ExistsAsync(legalEntityId, ct)) throw new NotFoundException("Legal entity");
        var now = clock.GetUtcNow();
        var run = new ReconciliationRun { Id = Guid.NewGuid(), LegalEntityId = legalEntityId, StartedBy = current.UserId, StartedAt = now };

        var resolved = await contexts.ResolveAsync(legalEntityId, ct);
        if (resolved.Error is not null) return await FailAsync(run, resolved.Error.Code, ct);

        var listed = await resolved.Adapter!.ListInvoicesAsync(resolved.Context!, null, ct);
        if (!listed.IsSuccess) return await FailAsync(run, listed.ErrorCode ?? "erp_error", ct);

        var adapter = resolved.Adapter;
        var found = new List<Finding>();
        var posted = await db.SyncRecords.AsNoTracking()
            .Where(r => r.LegalEntityId == legalEntityId && r.Type == SyncType.PostInvoice && (r.Status == SyncStatus.Succeeded || r.Status == SyncStatus.Resolved)).ToListAsync(ct);
        var byReference = listed.Items.Where(d => d.DocumentKey is not null).GroupBy(d => d.DocumentKey!).ToDictionary(g => g.Key, g => g.First());
        var byErpRef = listed.Items.GroupBy(d => d.Reference).ToDictionary(g => g.Key, g => g.First());
        var known = new HashSet<string>();

        foreach (var record in posted)
        {
            var invoice = await invoices.GetAsync(record.ObjectId, ct);
            if (invoice is null) continue;
            var h = invoice.Header;
            var doc = byReference.GetValueOrDefault(adapter.DocumentReference(record.IdempotencyKey)) ?? (record.ErpReference is { } r ? byErpRef.GetValueOrDefault(r) : null);
            var subject = h.Id.ToString("N");
            var vpms = $"{h.InvoiceNumber} {Money(h.Total)} {h.CurrencyCode}";

            if (doc is null)
            {
                found.Add(new(ReconType.MissingInErp, subject, h.Id, h.InvoiceNumber, record.ErpReference, vpms, "not found", ""));
                continue;
            }

            known.Add(doc.Reference);
            var erp = $"{doc.InvoiceNumber} {Money(doc.Total)} {doc.CurrencyCode}";
            if (Math.Abs(doc.Total - h.Total) > Tolerance || !string.Equals(doc.CurrencyCode, h.CurrencyCode, StringComparison.OrdinalIgnoreCase))
            {
                found.Add(new(ReconType.AmountMismatch, subject, h.Id, h.InvoiceNumber, doc.Reference, $"{Money(h.Total)} {h.CurrencyCode}", $"{Money(doc.Total)} {doc.CurrencyCode}", ""));
            }

            var vendor = await vendors.GetAsync(h.VendorId, legalEntityId, ct);
            if (vendor?.ErpAccount is { } account && !string.Equals(account, doc.VendorAccount, StringComparison.OrdinalIgnoreCase))
            {
                found.Add(new(ReconType.VendorMismatch, subject, h.Id, h.InvoiceNumber, doc.Reference, account, doc.VendorAccount, ""));
            }

            if (doc.State == ErpDocumentState.Cancelled)
            {
                found.Add(new(ReconType.StatusMismatch, subject, h.Id, h.InvoiceNumber, doc.Reference, "Approved, to be paid", "Cancelled in ERP", ""));
            }
            else if (doc.State == ErpDocumentState.Paid && !await payments.HasReleasedPaymentAsync(h.Id, ct))
            {
                found.Add(new(ReconType.PaidInErpNotReleased, subject, h.Id, h.InvoiceNumber, doc.Reference, "No payment released in VPMS", "Paid in ERP", ""));
            }
        }

        // Documents in the ERP for this entity's vendors that VPMS did not post.
        var accounts = (await vendors.ListActiveAsync(legalEntityId, 1000, ct)).Where(v => v.ErpAccount is not null).Select(v => v.ErpAccount!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var doc in listed.Items.Where(d => !known.Contains(d.Reference) && accounts.Contains(d.VendorAccount)))
        {
            found.Add(new(ReconType.UnknownInErp, doc.Reference, null, doc.InvoiceNumber, doc.Reference, "not posted by VPMS", $"{doc.InvoiceNumber} {Money(doc.Total)} {doc.CurrencyCode} ({doc.VendorAccount})", ""));
        }

        run.VpmsInvoices = posted.Count;
        run.ErpDocuments = listed.Items.Count;
        await ApplyAsync(run, found, ct);
        return Map(run);
    }

    private sealed record Finding(string Type, string Subject, Guid? InvoiceId, string? Number, string? ErpRef, string VpmsValue, string ErpValue, string Detail);

    private async Task ApplyAsync(ReconciliationRun run, List<Finding> found, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var existing = await db.ReconItems.Where(i => i.LegalEntityId == run.LegalEntityId).ToListAsync(ct);
        var keys = new HashSet<string>();
        int added = 0, brandNew = 0, cleared = 0;

        foreach (var f in found)
        {
            var key = ReconciliationItem.KeyFor(f.Type, f.Subject, f.VpmsValue, f.ErpValue);
            if (!keys.Add(key)) continue;
            var item = existing.FirstOrDefault(i => i.ItemKey == key);
            if (item is null)
            {
                db.ReconItems.Add(new ReconciliationItem
                {
                    Id = Guid.NewGuid(), LegalEntityId = run.LegalEntityId, Type = f.Type, ItemKey = key, InvoiceId = f.InvoiceId, InvoiceNumber = f.Number, ErpReference = f.ErpRef,
                    VpmsValue = f.VpmsValue, ErpValue = f.ErpValue, FirstSeenRunId = run.Id, LastSeenRunId = run.Id, FirstSeenAt = now, LastSeenAt = now,
                });
                added++;
                brandNew++;
            }
            else
            {
                item.Seen(run.Id, now);
                if (item.Status == ReconItemStatus.Cleared)
                {
                    // It came back after being cleared: open again, history kept in the audit trail.
                    item.Status = ReconItemStatus.Open;
                    item.DecisionReason = null;
                    item.DecidedAt = null;
                    added++;
                }
            }
        }

        foreach (var gone in existing.Where(i => !keys.Contains(i.ItemKey) && i.IsOpenForDecision))
        {
            gone.Clear(now);
            cleared++;
        }

        run.NewItems = added;
        run.ClearedItems = cleared;
        run.OpenItems = existing.Count(i => i.IsOpenForDecision) + brandNew;
        run.Status = ReconRunStatus.Completed;
        run.CompletedAt = clock.GetUtcNow();
        db.ReconRuns.Add(run);
        audit.Record(new AuditEntry("erp.recon.run", "ReconciliationRun", run.Id.ToString(), run.LegalEntityId,
            JsonSerializer.Serialize(new { run.VpmsInvoices, run.ErpDocuments, run.NewItems, run.OpenItems, run.ClearedItems })));
        await db.SaveChangesAsync(ct);
    }

    private async Task<ReconRunDto> FailAsync(ReconciliationRun run, string code, CancellationToken ct)
    {
        run.Status = ReconRunStatus.Failed;
        run.ErrorCode = code;
        run.CompletedAt = clock.GetUtcNow();
        db.ReconRuns.Add(run);
        audit.Record(new AuditEntry("erp.recon.failed", "ReconciliationRun", run.Id.ToString(), run.LegalEntityId, JsonSerializer.Serialize(new { code })));
        await db.SaveChangesAsync(ct);
        return Map(run);
    }

    // --- reading ---------------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<ReconRunDto>> RunsAsync(Guid? legalEntityId, IReadOnlySet<Guid>? entityScope, int take, CancellationToken ct = default)
    {
        var q = db.ReconRuns.AsNoTracking().AsQueryable();
        if (legalEntityId is { } e) q = q.Where(r => r.LegalEntityId == e);
        if (entityScope is not null) q = q.Where(r => entityScope.Contains(r.LegalEntityId));
        return (await q.OrderByDescending(r => r.StartedAt).Take(Math.Clamp(take, 1, 200)).ToListAsync(ct)).Select(Map).ToList();
    }

    public async Task<ReconSummaryDto> SummaryAsync(IReadOnlySet<Guid>? entityScope, CancellationToken ct = default)
    {
        var q = db.ReconItems.AsNoTracking().AsQueryable();
        if (entityScope is not null) q = q.Where(i => entityScope.Contains(i.LegalEntityId));
        var counts = await q.GroupBy(i => new { i.Status, i.Type }).Select(g => new { g.Key.Status, g.Key.Type, Count = g.Count() }).ToListAsync(ct);
        int Of(string s) => counts.Where(c => c.Status == s).Sum(c => c.Count);
        var open = counts.Where(c => c.Status is ReconItemStatus.Open or ReconItemStatus.PendingAcceptance).GroupBy(c => c.Type).ToDictionary(g => g.Key, g => g.Sum(c => c.Count));
        return new ReconSummaryDto(Of(ReconItemStatus.Open), Of(ReconItemStatus.PendingAcceptance), Of(ReconItemStatus.Accepted), Of(ReconItemStatus.Resolved), open);
    }

    public async Task<IReadOnlyList<ReconItemDto>> ItemsAsync(Guid? legalEntityId, string? status, string? type, IReadOnlySet<Guid>? entityScope, int skip, int take, CancellationToken ct = default) =>
        (await Filter(legalEntityId, status, type, entityScope).OrderBy(i => i.Status).ThenByDescending(i => i.LastSeenAt).Skip(Math.Max(0, skip)).Take(Math.Clamp(take, 1, 500)).ToListAsync(ct))
        .Select(Map).ToList();

    public async Task<string> ExportCsvAsync(Guid? legalEntityId, string? status, IReadOnlySet<Guid>? entityScope, CancellationToken ct = default)
    {
        var rows = await Filter(legalEntityId, status, null, entityScope).OrderBy(i => i.LegalEntityId).ThenBy(i => i.Type).ThenBy(i => i.InvoiceNumber).Take(10_000).ToListAsync(ct);
        var sb = new StringBuilder();
        sb.AppendLine("Type,Status,LegalEntityId,InvoiceNumber,ErpReference,VpmsValue,ErpValue,FirstSeen,LastSeen,DecisionReason");
        foreach (var r in rows)
        {
            sb.AppendLine(string.Join(',', Cell(r.Type), Cell(r.Status), Cell(r.LegalEntityId.ToString()), Cell(r.InvoiceNumber), Cell(r.ErpReference), Cell(r.VpmsValue), Cell(r.ErpValue),
                Cell(r.FirstSeenAt.ToString("O", CultureInfo.InvariantCulture)), Cell(r.LastSeenAt.ToString("O", CultureInfo.InvariantCulture)), Cell(r.DecisionReason)));
        }

        return sb.ToString();
    }

    public async Task<Guid> EntityOfItemAsync(Guid itemId, CancellationToken ct = default) =>
        await db.ReconItems.AsNoTracking().Where(i => i.Id == itemId).Select(i => (Guid?)i.LegalEntityId).FirstOrDefaultAsync(ct) ?? throw new NotFoundException("Reconciliation item");

    // --- deciding --------------------------------------------------------------------------------------------

    public Task<ReconItemDto> ResolveAsync(Guid itemId, string reason, CancellationToken ct = default) =>
        DecideAsync(itemId, "erp.recon.item.resolved", i => i.Resolve(current.UserId, reason, clock.GetUtcNow()), ct);

    public Task<ReconItemDto> ProposeAcceptanceAsync(Guid itemId, string reason, CancellationToken ct = default) =>
        DecideAsync(itemId, "erp.recon.item.acceptance-proposed", i => i.ProposeAcceptance(current.UserId, reason, clock.GetUtcNow()), ct);

    public Task<ReconItemDto> ApproveAcceptanceAsync(Guid itemId, string? comment, CancellationToken ct = default) =>
        DecideAsync(itemId, "erp.recon.item.accepted", i => i.ApproveAcceptance(current.UserId, comment, clock.GetUtcNow()), ct);

    public Task<ReconItemDto> RejectAcceptanceAsync(Guid itemId, string reason, CancellationToken ct = default) =>
        DecideAsync(itemId, "erp.recon.item.acceptance-rejected", i => i.RejectAcceptance(current.UserId, reason, clock.GetUtcNow()), ct);

    private async Task<ReconItemDto> DecideAsync(Guid itemId, string action, Action<ReconciliationItem> change, CancellationToken ct)
    {
        var item = await db.ReconItems.FirstOrDefaultAsync(i => i.Id == itemId, ct) ?? throw new NotFoundException("Reconciliation item");
        try
        {
            change(item);
        }
        catch (SegregationOfDutiesException ex)
        {
            await audit.WriteNowAsync(new AuditEntry("sod.erp-recon.blocked", "ReconciliationItem", itemId.ToString(), item.LegalEntityId, JsonSerializer.Serialize(new { attempted = action, rule = ex.Rule })), ct);
            throw;
        }

        audit.Record(new AuditEntry(action, "ReconciliationItem", itemId.ToString(), item.LegalEntityId,
            JsonSerializer.Serialize(new { item.Type, item.Status, item.InvoiceId, reason = item.DecisionReason ?? item.ProposedReason })));
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConflictException("concurrent_change", "The item was changed at the same time. Reload and try again.");
        }

        return Map(item);
    }

    // --- helpers ---------------------------------------------------------------------------------------------

    private IQueryable<ReconciliationItem> Filter(Guid? legalEntityId, string? status, string? type, IReadOnlySet<Guid>? entityScope)
    {
        var q = db.ReconItems.AsNoTracking().AsQueryable();
        if (legalEntityId is { } e) q = q.Where(i => i.LegalEntityId == e);
        if (entityScope is not null) q = q.Where(i => entityScope.Contains(i.LegalEntityId));
        if (!string.IsNullOrWhiteSpace(status)) q = q.Where(i => i.Status == status);
        if (!string.IsNullOrWhiteSpace(type)) q = q.Where(i => i.Type == type);
        return q;
    }

    private static string Money(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>Quotes a CSV cell and defuses leading characters that make a spreadsheet read it as a formula.</summary>
    public static string Cell(string? value)
    {
        var text = value ?? "";
        if (text.Length > 0 && "=+-@\t\r".Contains(text[0])) text = "'" + text;
        return "\"" + text.Replace("\"", "\"\"") + "\"";
    }

    private static ReconRunDto Map(ReconciliationRun r) => new(r.Id, r.LegalEntityId, r.StartedAt, r.CompletedAt, r.Status, r.ErrorCode, r.VpmsInvoices, r.ErpDocuments, r.NewItems, r.OpenItems, r.ClearedItems);

    private static ReconItemDto Map(ReconciliationItem i) => new(
        i.Id, i.LegalEntityId, i.Type, i.InvoiceId, i.InvoiceNumber, i.ErpReference, i.VpmsValue, i.ErpValue, i.Status, i.FirstSeenAt, i.LastSeenAt, i.ProposedBy, i.ProposedReason, i.DecidedBy,
        i.DecidedAt, i.DecisionReason);
}
