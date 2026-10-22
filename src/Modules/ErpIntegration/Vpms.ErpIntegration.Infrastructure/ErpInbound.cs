using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vpms.BuildingBlocks.Audit;
using Vpms.BuildingBlocks.Tenancy;
using Vpms.ErpIntegration.Application;
using Vpms.ErpIntegration.Contracts;
using Vpms.ErpIntegration.Domain;
using Vpms.Invoices.Contracts;

namespace Vpms.ErpIntegration.Infrastructure;

/// <summary>
/// Reads what the ERP knows and records it. Everything arrives through the inbox, whose message id is built from the content, so a
/// replayed page or an overlapping pull window changes nothing. ERP data never edits an approved invoice and never causes a payment: it is
/// stored, shown, and compared in reconciliation.
/// </summary>
public sealed class ErpInbound(
    ErpDbContext db, ErpContextResolver contexts, IPurchaseOrderSink purchaseOrders, ITenantContext tenant, IAuditPort audit, TimeProvider clock) : IErpInbound, IErpInboundQuery
{
    private static readonly TimeSpan Overlap = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan FirstPaymentLookback = TimeSpan.FromDays(90);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<InboundResult> PullAsync(CancellationToken ct = default)
    {
        var tenantId = tenant.RequireTenantId();
        if (!await db.Configs.AnyAsync(c => c.Kind == ConfigKind.Connection && c.Status == ConfigStatus.Active, ct)) return new InboundResult(0, 0, 0, 0, []);
        var entities = await db.Configs.AsNoTracking().Where(c => c.Kind == ConfigKind.Mapping && c.Status == ConfigStatus.Active).Select(c => c.LegalEntityId!.Value).ToListAsync(ct);

        int received = 0, duplicates = 0, applied = 0, failed = 0;
        var errors = new List<string>();
        foreach (var entityId in entities)
        {
            var resolved = await contexts.ResolveAsync(entityId, ct);
            if (resolved.Error is not null)
            {
                errors.Add($"{entityId}: {resolved.Error.Code}");
                continue;
            }

            var batch = new List<ErpInboxMessage>();
            foreach (var kind in InboundKind.All)
            {
                var pull = await PullKindAsync(tenantId, entityId, kind, resolved, ct);
                if (pull.Error is not null) errors.Add($"{entityId}/{kind}: {pull.Error}");
                batch.AddRange(pull.Messages);
                duplicates += pull.Duplicates;
            }

            received += batch.Count;
            var outcome = await ApplyAsync(entityId, batch.Concat(await db.Inbox.Where(m => m.LegalEntityId == entityId && m.Status == InboxStatus.Failed && m.Kind == InboundKind.PurchaseOrder).ToListAsync(ct)).Distinct().ToList(), ct);
            applied += outcome.Applied;
            failed += outcome.Failed;
        }

        if (received > 0) audit.Record(new AuditEntry("erp.inbound.received", "ErpInbox", null, null, JsonSerializer.Serialize(new { received, duplicates, applied, failed }), ActorType: AuditActorType.System));
        await db.SaveChangesAsync(ct);
        return new InboundResult(received, duplicates, applied, failed, errors);
    }

    public async Task<ErpInvoiceStatusDto?> InvoiceStatusAsync(Guid invoiceId, CancellationToken ct = default)
    {
        var message = await db.Inbox.AsNoTracking().Where(m => m.Kind == InboundKind.InvoiceStatus && m.ExternalId == invoiceId.ToString("N")).OrderByDescending(m => m.Sequence).FirstOrDefaultAsync(ct);
        if (message is null) return null;
        var s = JsonSerializer.Deserialize<InvoiceStatusPayload>(message.PayloadJson, Json)!;
        return new ErpInvoiceStatusDto(s.Reference, s.State, s.PaidAt, s.Total, s.CurrencyCode, message.ReceivedAt);
    }

    // --- pulling ---------------------------------------------------------------------------------------------

    private sealed record Pulled(IReadOnlyList<ErpInboxMessage> Messages, int Duplicates, string? Error);

    private async Task<Pulled> PullKindAsync(Guid tenantId, Guid entityId, string kind, ResolvedErp erp, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var state = await db.PullStates.FirstOrDefaultAsync(s => s.LegalEntityId == entityId && s.Kind == kind, ct);
        if (state is null)
        {
            state = new ErpPullState { Id = Guid.NewGuid(), TenantId = tenantId, LegalEntityId = entityId, Kind = kind };
            db.PullStates.Add(state);
        }

        DateTimeOffset? since = state.LastSuccessAt is { } last ? last - Overlap : null;
        var items = new List<(string ExternalId, object Payload)>();
        string? error = null;
        switch (kind)
        {
            case InboundKind.PurchaseOrder:
            {
                var r = await erp.Adapter!.ListPurchaseOrdersAsync(erp.Context!, since, ct);
                if (!r.IsSuccess) error = r.ErrorCode;
                else items.AddRange(r.Items.Select(o => (o.PoNumber, (object)o)));
                break;
            }

            case InboundKind.InvoiceStatus:
            {
                var posted = await db.SyncRecords.AsNoTracking().Where(x => x.LegalEntityId == entityId && x.Type == SyncType.PostInvoice && (x.Status == SyncStatus.Succeeded || x.Status == SyncStatus.Resolved)).ToListAsync(ct);
                var byReference = posted.ToDictionary(x => erp.Adapter!.DocumentReference(x.IdempotencyKey), x => x.ObjectId);
                var r = await erp.Adapter!.ListInvoicesAsync(erp.Context!, since, ct);
                if (!r.IsSuccess) error = r.ErrorCode;
                else
                {
                    foreach (var doc in r.Items.Where(d => d.DocumentKey is not null && byReference.ContainsKey(d.DocumentKey)))
                    {
                        var invoiceId = byReference[doc.DocumentKey!];
                        items.Add((invoiceId.ToString("N"), new InvoiceStatusPayload(invoiceId, doc.Reference, doc.State, doc.PaidAt, doc.Total, doc.CurrencyCode, doc.VendorAccount, doc.InvoiceNumber)));
                    }
                }

                break;
            }

            case InboundKind.Payment:
            {
                var r = await erp.Adapter!.GetVendorPaymentsAsync(erp.Context!, since ?? now - FirstPaymentLookback, ct);
                if (!r.IsSuccess) error = r.ErrorCode;
                else
                {
                    var refs = await db.SyncRecords.AsNoTracking().Where(x => x.LegalEntityId == entityId && x.Type == SyncType.PostInvoice && x.ErpReference != null).Select(x => new { x.ErpReference, x.ObjectId }).ToListAsync(ct);
                    var byRef = refs.GroupBy(x => x.ErpReference!).ToDictionary(g => g.Key, g => g.First().ObjectId);
                    foreach (var p in r.Items) items.Add((p.Reference, new PaymentPayload(p.Reference, p.VendorAccount, p.InvoiceReference, p.InvoiceReference is not null && byRef.TryGetValue(p.InvoiceReference, out var id) ? id : null, p.Amount, p.CurrencyCode, p.PaidAt)));
                }

                break;
            }
        }

        state.LastPulledAt = now;
        state.LastError = error;
        if (error is not null) return new Pulled([], 0, error);
        state.LastSuccessAt = now;

        var messages = new List<ErpInboxMessage>();
        var duplicates = 0;
        foreach (var (externalId, payload) in items)
        {
            var json = JsonSerializer.Serialize(payload, Json);
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant()[..32];
            var messageId = ErpInboxMessage.IdFor(tenantId, entityId, kind, externalId, hash);
            if (messages.Any(m => m.MessageId == messageId) || await db.Inbox.AnyAsync(m => m.MessageId == messageId, ct))
            {
                duplicates++;
                continue;
            }

            var sequence = (await db.Inbox.Where(m => m.LegalEntityId == entityId && m.Kind == kind && m.ExternalId == externalId).MaxAsync(m => (int?)m.Sequence, ct) ?? 0) + 1
                           + messages.Count(m => m.ExternalId == externalId);
            var message = new ErpInboxMessage
            {
                Id = Guid.NewGuid(), TenantId = tenantId, LegalEntityId = entityId, Kind = kind, ExternalId = externalId, ContentHash = hash, MessageId = messageId, Sequence = sequence,
                PayloadJson = json, ReceivedAt = now,
            };
            db.Inbox.Add(message);
            messages.Add(message);
        }

        return new Pulled(messages, duplicates, null);
    }

    // --- applying --------------------------------------------------------------------------------------------

    private async Task<(int Applied, int Failed)> ApplyAsync(Guid entityId, IReadOnlyList<ErpInboxMessage> messages, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        int applied = 0, failed = 0;

        var orders = messages.Where(m => m.Kind == InboundKind.PurchaseOrder).ToList();
        if (orders.Count > 0)
        {
            var items = orders.Select(m =>
            {
                var o = JsonSerializer.Deserialize<ErpPurchaseOrder>(m.PayloadJson, Json)!;
                return (Message: m, Item: new PoSinkItem(o.PoNumber, Math.Max(o.Version, m.Sequence), o.VendorAccount, o.CurrencyCode, o.Status, o.OrderDate, o.InvoicedValue,
                    o.Lines.Select(l => new PoSinkLine(l.LineNo, l.Description, l.OrderedQuantity, l.UnitPrice, l.InvoicedQuantity)).ToList()));
            }).ToList();

            var result = await purchaseOrders.ImportAsync(entityId, items.Select(i => i.Item).ToList(), ct);
            foreach (var (message, item) in items)
            {
                var error = result.Errors.FirstOrDefault(e => e.PoNumber == item.PoNumber);
                if (error is null)
                {
                    message.MarkApplied(now);
                    applied++;
                }
                else
                {
                    message.MarkFailed($"{error.Code}: {error.Message}");
                    failed++;
                }
            }
        }

        foreach (var m in messages.Where(m => m.Kind != InboundKind.PurchaseOrder && m.Status == InboxStatus.Received))
        {
            m.MarkApplied(now);
            applied++;
        }

        return (applied, failed);
    }

    private sealed record InvoiceStatusPayload(Guid InvoiceId, string Reference, string State, DateTimeOffset? PaidAt, decimal Total, string CurrencyCode, string VendorAccount, string InvoiceNumber);

    private sealed record PaymentPayload(string Reference, string VendorAccount, string? InvoiceReference, Guid? InvoiceId, decimal Amount, string CurrencyCode, DateTimeOffset PaidAt);
}
