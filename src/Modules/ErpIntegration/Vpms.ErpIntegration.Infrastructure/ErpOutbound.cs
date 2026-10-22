using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Vpms.BuildingBlocks.Audit;
using Vpms.BuildingBlocks.Errors;
using Vpms.BuildingBlocks.Tenancy;
using Vpms.Documents.Contracts;
using Vpms.ErpIntegration.Application;
using Vpms.ErpIntegration.Contracts;
using Vpms.ErpIntegration.Domain;
using Vpms.Invoices.Contracts;
using Vpms.Vendors.Contracts;

namespace Vpms.ErpIntegration.Infrastructure;

/// <summary>Spreads retries out. Replaced in tests to make timing exact.</summary>
public class ErpJitter
{
    public virtual double Next() => Random.Shared.NextDouble();
}

/// <summary>
/// The outbound queue: queues what must reach the ERP (transactional outbox with an idempotency key per business event) and sends what is
/// due. Delivery is at-least-once, so every send is idempotent in the ERP; a message whose subject changed since it was queued is retired
/// instead of sent.
/// </summary>
public sealed class ErpOutbound(
    ErpDbContext db, ErpContextResolver contexts, IInvoicePostingSource invoices, IVendorErpSource vendors, IAttachmentService attachments, ITenantContext tenant,
    IAuditPort audit, TimeProvider clock, ErpJitter jitter, ILogger<ErpOutbound> logger) : IErpOutbound
{
    private const int MaxAttachmentBytes = 25 * 1024 * 1024;
    private static readonly TimeSpan ConfigDeferral = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan VendorDeferral = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan DeferralLimit = TimeSpan.FromDays(2);
    private readonly string _owner = $"{Environment.MachineName}:{Guid.NewGuid():N}";

    // --- queueing --------------------------------------------------------------------------------------------

    public async Task<int> EnqueueAsync(CancellationToken ct = default)
    {
        var tenantId = tenant.RequireTenantId();
        if (!await db.Configs.AnyAsync(c => c.Kind == ConfigKind.Connection && c.Status == ConfigStatus.Active, ct)) return 0;
        var entities = await db.Configs.AsNoTracking().Where(c => c.Kind == ConfigKind.Mapping && c.Status == ConfigStatus.Active).Select(c => c.LegalEntityId!.Value).ToListAsync(ct);

        var queued = 0;
        foreach (var entityId in entities)
        {
            queued += await EnqueueVendorsAsync(tenantId, entityId, ct);
            queued += await EnqueueInvoicesAsync(tenantId, entityId, ct);
        }

        return queued;
    }

    private async Task<int> EnqueueVendorsAsync(Guid tenantId, Guid entityId, CancellationToken ct)
    {
        // The vendors of one legal entity are few enough to read in full on every sweep; the keys decide which are new or changed.
        var page = await vendors.ListActiveAsync(entityId, 1000, ct);
        if (page.Count == 0) return 0;
        var keys = page.ToDictionary(v => ErpSyncRecord.KeyFor(tenantId, entityId, SyncType.UpsertVendor, v.VendorId, v.SyncHash));
        var known = (await db.SyncRecords.AsNoTracking().Where(r => keys.Keys.Contains(r.IdempotencyKey)).Select(r => r.IdempotencyKey).ToListAsync(ct)).ToHashSet();
        var added = 0;
        foreach (var (key, v) in keys.Where(k => !known.Contains(k.Key)))
        {
            var row = New(tenantId, entityId, SyncType.UpsertVendor, v.VendorId, v.SyncHash, v.SyncHash, key, null);
            db.SyncRecords.Add(row);
            audit.Record(Entry("erp.sync.enqueued", row, new { type = SyncType.UpsertVendor, v.VendorId }));
            added++;
        }

        if (added > 0) await SaveQueuedAsync(ct);
        return added;
    }

    private async Task<int> EnqueueInvoicesAsync(Guid tenantId, Guid entityId, CancellationToken ct)
    {
        var watermark = await db.SyncRecords.AsNoTracking().Where(r => r.LegalEntityId == entityId && r.Type == SyncType.PostInvoice).MaxAsync(r => r.SourceAt, ct);
        var approved = await invoices.ListApprovedAsync(entityId, watermark, 200, ct);
        if (approved.Count == 0) return 0;

        var keys = approved.ToDictionary(i => i.Header.Id, i => ErpSyncRecord.KeyFor(tenantId, entityId, SyncType.PostInvoice, i.Header.Id, i.Header.ApprovedVersion!.Value.ToString()));
        var known = await db.SyncRecords.AsNoTracking().Where(r => keys.Values.Contains(r.IdempotencyKey)).Select(r => r.IdempotencyKey).ToListAsync(ct);
        var added = 0;
        foreach (var inv in approved.Where(i => !known.Contains(keys[i.Header.Id])))
        {
            var h = inv.Header;
            var version = h.ApprovedVersion!.Value.ToString();
            var row = New(tenantId, entityId, SyncType.PostInvoice, h.Id, version, h.IntegrityHash, keys[h.Id], h.ApprovedAt);

            // An earlier version of this invoice already reached the ERP. Posting a second document would duplicate it, so a person decides.
            var earlier = await db.SyncRecords.AsNoTracking().AnyAsync(r => r.Type == SyncType.PostInvoice && r.ObjectId == h.Id
                                                                           && (r.Status == SyncStatus.Succeeded || r.Status == SyncStatus.Resolved) && r.ObjectVersion != version, ct);
            if (earlier)
            {
                row.DeadLetterOnArrival("already_posted_other_version", "An earlier version of this invoice is already in the ERP. Correct it there, then mark this resolved.", clock.GetUtcNow());
            }

            db.SyncRecords.Add(row);
            audit.Record(Entry("erp.sync.enqueued", row, new { type = SyncType.PostInvoice, invoiceId = h.Id, version, status = row.Status }));
            added++;
        }

        if (added > 0) await SaveQueuedAsync(ct);
        return added;
    }

    private ErpSyncRecord New(Guid tenantId, Guid entityId, string type, Guid objectId, string version, string hash, string key, DateTimeOffset? sourceAt) => new()
    {
        Id = Guid.NewGuid(), TenantId = tenantId, LegalEntityId = entityId, Type = type, ObjectId = objectId, ObjectVersion = version, PayloadHash = hash, IdempotencyKey = key,
        Status = SyncStatus.Pending, NextAttemptAt = clock.GetUtcNow(), CreatedAt = clock.GetUtcNow(), SourceAt = sourceAt,
    };

    /// <summary>Two sweeps racing to queue the same event: the unique key lets exactly one win; the loser just moves on.</summary>
    private async Task SaveQueuedAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsDuplicate(ex))
        {
            db.ChangeTracker.Clear();
            logger.LogInformation("Another worker queued the same ERP messages first.");
        }
    }

    private static bool IsDuplicate(DbUpdateException ex) => ex.InnerException?.Message.Contains("duplicate key", StringComparison.OrdinalIgnoreCase) == true;

    // --- sending ---------------------------------------------------------------------------------------------

    public async Task<SyncRunResult> ProcessDueAsync(int batch, CancellationToken ct = default)
    {
        int processed = 0, succeeded = 0, retrying = 0, dead = 0;
        var now = clock.GetUtcNow();
        var due = await db.SyncRecords.Where(r => (r.Status == SyncStatus.Pending && r.NextAttemptAt <= now) || (r.Status == SyncStatus.InFlight && r.LeaseUntil <= now))
            .OrderBy(r => r.NextAttemptAt).ThenBy(r => r.Type == SyncType.UpsertVendor ? 0 : 1).Take(Math.Clamp(batch, 1, 200)).ToListAsync(ct);

        foreach (var record in due)
        {
            try
            {
                record.Lease(_owner, clock.GetUtcNow());
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                // Another worker leased it first. It is theirs now.
                db.Entry(record).State = EntityState.Detached;
                continue;
            }

            processed++;
            ErpResult result;
            try
            {
                result = await SendAsync(record, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unexpected failure sending ERP message {RecordId}.", record.Id);
                result = ErpResult.Transient("unexpected_error", ex.GetType().Name);
            }

            switch (Apply(record, result))
            {
                case SyncStatus.Succeeded: succeeded++; break;
                case SyncStatus.Pending: retrying++; break;
                case SyncStatus.DeadLetter: dead++; break;
            }

            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                // The lease ran out and another worker took over while we were talking to the ERP. The send was idempotent, so nothing is duplicated.
                logger.LogWarning("Lost the lease on ERP message {RecordId} while sending.", record.Id);
                db.ChangeTracker.Clear();
            }
        }

        return new SyncRunResult(0, processed, succeeded, retrying, dead);
    }

    /// <summary>The record's new status after applying the result (also writes the attempt row and audit entry).</summary>
    private string Apply(ErpSyncRecord record, ErpResult result)
    {
        var now = clock.GetUtcNow();
        db.SyncAttempts.Add(new ErpSyncAttempt
        {
            Id = Guid.NewGuid(), TenantId = record.TenantId, RecordId = record.Id, AttemptNo = Math.Max(1, record.Attempts), At = now, Outcome = result.Outcome.ToString(),
            Code = result.ErrorCode, Message = result.Message is { Length: > 500 } m ? m[..500] : result.Message,
        });

        if (record.Status != SyncStatus.InFlight) return record.Status; // retired or deferred inside the handler

        switch (result.Outcome)
        {
            case ErpOutcome.Succeeded or ErpOutcome.AlreadyExists:
                record.Succeed(result.Reference ?? "", now);
                audit.Record(Entry("erp.sync.succeeded", record, new { record.Type, record.ObjectId, reference = record.ErpReference, existing = result.Outcome == ErpOutcome.AlreadyExists }));
                break;
            case ErpOutcome.Rejected:
                record.DeadLetterNow(result.ErrorCode ?? "rejected", result.Message, now);
                audit.Record(Entry("erp.sync.dead-lettered", record, new { record.Type, record.ObjectId, code = record.LastErrorCode }));
                break;
            default:
                record.FailTransient(result.ErrorCode ?? "transient", result.Message, now, result.RetryAfter, jitter.Next());
                if (record.Status == SyncStatus.DeadLetter)
                {
                    audit.Record(Entry("erp.sync.dead-lettered", record, new { record.Type, record.ObjectId, code = record.LastErrorCode }));
                }

                break;
        }

        return record.Status;
    }

    private async Task<ErpResult> SendAsync(ErpSyncRecord record, CancellationToken ct)
    {
        var resolved = await contexts.ResolveAsync(record.LegalEntityId, ct);
        if (resolved.Error is not null) return Defer(record, resolved.Error.Code, resolved.Error.Message, ConfigDeferral);

        return record.Type switch
        {
            SyncType.PostInvoice => await PostInvoiceAsync(record, resolved, ct),
            SyncType.UpsertVendor => await UpsertVendorAsync(record, resolved, ct),
            _ => DeadLetter(record, "unknown_type", "Unknown message type."),
        };
    }

    private async Task<ErpResult> PostInvoiceAsync(ErpSyncRecord record, ResolvedErp erp, CancellationToken ct)
    {
        var invoice = await invoices.GetAsync(record.ObjectId, ct);
        var h = invoice?.Header;
        if (invoice is null || h is null || !h.IsPayable || h.ApprovedVersion.ToString() != record.ObjectVersion || h.IntegrityHash != record.PayloadHash)
        {
            return Supersede(record, "The invoice changed after this message was queued; a newer message replaces it.");
        }

        var vendor = await vendors.GetAsync(h.VendorId, record.LegalEntityId, ct);
        if (vendor is null) return DeadLetter(record, "vendor_not_active", "The vendor is not an active vendor of this legal entity.");
        if (string.IsNullOrWhiteSpace(vendor.ErpAccount)) return Defer(record, "vendor_not_synced", "The vendor has no ERP account yet.", VendorDeferral);

        var mapped = InvoiceMapper.Map(invoice, vendor.ErpAccount, record.IdempotencyKey, erp.Context!.Company);
        if (mapped.Problem is not null) return DeadLetter(record, mapped.Problem.Code, mapped.Problem.Message);

        var created = await erp.Adapter!.CreateVendorInvoiceAsync(erp.Context, mapped.Invoice!, ct);
        if (!created.IsSuccess) return created;

        foreach (var file in invoice.Attachments.Where(a => a.ScanStatus == ScanStatus.Clean && a.SizeBytes <= MaxAttachmentBytes))
        {
            byte[] content;
            var download = await attachments.OpenDownloadAsync(file.AttachmentId, ct);
            await using (download.Content)
            {
                using var buffer = new MemoryStream();
                await download.Content.CopyToAsync(buffer, ct);
                content = buffer.ToArray();
            }

            var attached = await erp.Adapter.AttachDocumentAsync(erp.Context, created.Reference!, new ErpAttachment(file.FileName, file.ContentType, content), $"{record.IdempotencyKey}:att:{file.AttachmentId:N}", ct);
            if (!attached.IsSuccess) return attached;
        }

        return created;
    }

    private async Task<ErpResult> UpsertVendorAsync(ErpSyncRecord record, ResolvedErp erp, CancellationToken ct)
    {
        var vendor = await vendors.GetAsync(record.ObjectId, record.LegalEntityId, ct);
        if (vendor is null) return Supersede(record, "The vendor is no longer an active vendor of this legal entity.");
        if (vendor.SyncHash != record.PayloadHash) return Supersede(record, "The vendor changed after this message was queued; a newer message replaces it.");

        var data = new ErpVendor(vendor.VendorId, vendor.LegalName, vendor.TaxNumber, vendor.CountryCode, vendor.CurrencyCode, vendor.PaymentTermsCode, vendor.ErpAccount);
        var result = await erp.Adapter!.UpsertVendorAsync(erp.Context!, data, record.IdempotencyKey, ct);
        if (!result.IsSuccess) return result;

        try
        {
            await vendors.LinkErpAccountAsync(vendor.VendorId, record.LegalEntityId, result.Reference!, ct);
        }
        catch (ConflictException ex)
        {
            return DeadLetter(record, ex.Code, "The ERP returned an account different from the one already linked to this vendor.");
        }

        return result;
    }

    // --- outcomes decided inside a handler ---------------------------------------------------------------------

    private ErpResult Defer(ErpSyncRecord record, string code, string? message, TimeSpan delay)
    {
        var now = clock.GetUtcNow();
        if (now - record.CreatedAt > DeferralLimit)
        {
            return DeadLetter(record, code, $"Could not be sent for {DeferralLimit.TotalDays:0} days: {message}");
        }

        record.Defer(code, message, now, delay);
        return ErpResult.Transient(code, message);
    }

    private ErpResult DeadLetter(ErpSyncRecord record, string code, string message) => ErpResult.Rejected(code, message);

    private ErpResult Supersede(ErpSyncRecord record, string note)
    {
        record.Supersede(note, clock.GetUtcNow());
        audit.Record(Entry("erp.sync.superseded", record, new { record.Type, record.ObjectId }));
        return ErpResult.Transient("superseded", note);
    }

    private static AuditEntry Entry(string action, ErpSyncRecord record, object detail) =>
        new(action, "ErpSyncRecord", record.Id.ToString(), record.LegalEntityId, JsonSerializer.Serialize(new { key = record.IdempotencyKey, detail }), ActorType: AuditActorType.System);
}
