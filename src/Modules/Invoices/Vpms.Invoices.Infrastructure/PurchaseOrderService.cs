using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vpms.BuildingBlocks.Audit;
using Vpms.BuildingBlocks.Errors;
using Vpms.BuildingBlocks.Security;
using Vpms.CustomerAdmin.Contracts;
using Vpms.Invoices.Application;
using Vpms.Invoices.Domain.Verification;
using Vpms.Vendors.Contracts;
using Vpms.Invoices.Domain;

namespace Vpms.Invoices.Infrastructure;

public sealed class PurchaseOrderService(
    InvoicesDbContext db, IVendorLookup vendors, ILegalEntityLookup entities, IAuditPort audit, ICurrentUser current, TimeProvider clock) : IPurchaseOrderService
{
    public const int MaxItemsPerImport = 500;
    private const int MaxLines = 1000;

    public async Task<PoImportResult> ImportAsync(Guid legalEntityId, IReadOnlyList<PoImportItem> items, CancellationToken ct = default)
    {
        if (!await entities.ExistsAsync(legalEntityId, ct)) throw new ValidationFailedException("unknown_legal_entity", "The legal entity does not exist.");
        if (items.Count == 0 || items.Count > MaxItemsPerImport) throw new ValidationFailedException("invalid_import", $"Import between 1 and {MaxItemsPerImport} purchase orders at a time.");

        int created = 0, updated = 0, unchanged = 0;
        var errors = new List<PoImportError>();
        foreach (var item in items)
        {
            try
            {
                switch (await ImportOneAsync(legalEntityId, item, ct))
                {
                    case Imported.Created: created++; break;
                    case Imported.Updated: updated++; break;
                    default: unchanged++; break;
                }
            }
            catch (Exception ex) when (ex is ValidationFailedException or ConflictException)
            {
                db.ChangeTracker.Clear();
                errors.Add(new PoImportError(item.PoNumber, ex is ValidationFailedException v ? v.Code : ((ConflictException)ex).Code, ex.Message));
            }
        }

        audit.Record(new AuditEntry("purchase-orders.imported", "PurchaseOrder", null, legalEntityId,
            JsonSerializer.Serialize(new { created, updated, unchanged, errors = errors.Count })));
        await db.SaveChangesAsync(ct);
        return new PoImportResult(created, updated, unchanged, errors);
    }

    private enum Imported { Created, Updated, Unchanged }

    private async Task<Imported> ImportOneAsync(Guid legalEntityId, PoImportItem item, CancellationToken ct)
    {
        Validate(item);
        var vendorId = item.VendorId;
        if (vendorId is null && !string.IsNullOrWhiteSpace(item.ErpVendorAccount)) vendorId = await vendors.FindByErpAccountAsync(item.ErpVendorAccount.Trim(), legalEntityId, ct);
        if (vendorId is null || await vendors.FindAsync(vendorId.Value, ct) is null)
        {
            throw new ValidationFailedException("unknown_vendor", $"The vendor of purchase order {item.PoNumber} is not known.");
        }

        var number = item.PoNumber.Trim();
        var hash = Hash(vendorId.Value, item);
        var existing = await db.PurchaseOrders.Include(p => p.Lines).FirstOrDefaultAsync(p => p.LegalEntityId == legalEntityId && p.PoNumber == number, ct);

        if (existing is not null)
        {
            if (item.Version < existing.Version) throw new ConflictException("po_older_version", $"Purchase order {number} is already at version {existing.Version}.");
            if (item.Version == existing.Version)
            {
                if (existing.ContentHash == hash) return Imported.Unchanged;
                throw new ConflictException("po_version_conflict", $"Purchase order {number} version {item.Version} exists with different content. Send a higher version.");
            }
        }

        var po = existing ?? new PurchaseOrder { Id = Guid.NewGuid(), LegalEntityId = legalEntityId, PoNumber = number };
        if (existing is null) db.PurchaseOrders.Add(po);
        else db.PurchaseOrderLines.RemoveRange(existing.Lines);

        po.VendorId = vendorId.Value;
        po.Version = item.Version;
        po.CurrencyCode = item.CurrencyCode;
        po.Status = item.Status;
        po.OrderDate = item.OrderDate;
        po.PreviouslyInvoicedValue = item.PreviouslyInvoicedValue;
        po.ContentHash = hash;
        po.ImportedBy = current.UserId;
        po.ImportedAt = clock.GetUtcNow();

        var lines = item.Lines.Select(l => new PurchaseOrderLine
        {
            Id = Guid.NewGuid(), TenantId = po.TenantId, VendorId = po.VendorId, PurchaseOrderId = po.Id, LineNo = l.LineNo.Trim(), Description = l.Description.Trim(),
            OrderedQuantity = l.OrderedQuantity, UnitPrice = l.UnitPrice, PreviouslyInvoicedQuantity = l.PreviouslyInvoicedQuantity,
        }).ToList();
        if (existing is null) po.Lines = lines;
        else
        {
            po.Lines = lines;
            db.PurchaseOrderLines.AddRange(lines);
        }

        await db.SaveChangesAsync(ct);
        return existing is null ? Imported.Created : Imported.Updated;
    }

    public async Task<IReadOnlyList<PurchaseOrderDto>> ListAsync(Guid? legalEntityId, string? search, IReadOnlySet<Guid>? entityScope, int skip, int take, CancellationToken ct = default)
    {
        var q = db.PurchaseOrders.AsNoTracking().Include(p => p.Lines).AsQueryable();
        if (entityScope is not null) q = q.Where(p => entityScope.Contains(p.LegalEntityId));
        if (legalEntityId is { } e) q = q.Where(p => p.LegalEntityId == e);
        if (!string.IsNullOrWhiteSpace(search)) q = q.Where(p => p.PoNumber.Contains(search.Trim()));
        return (await q.OrderBy(p => p.PoNumber).Skip(Math.Max(0, skip)).Take(Math.Clamp(take, 1, 200)).ToListAsync(ct)).Select(Map).ToList();
    }

    public async Task<PurchaseOrderDto> GetAsync(Guid id, CancellationToken ct = default) =>
        Map(await db.PurchaseOrders.AsNoTracking().Include(p => p.Lines).FirstOrDefaultAsync(p => p.Id == id, ct) ?? throw new NotFoundException("Purchase order"));

    public async Task<Guid> EntityOfAsync(Guid id, CancellationToken ct = default) =>
        await db.PurchaseOrders.AsNoTracking().Where(p => p.Id == id).Select(p => (Guid?)p.LegalEntityId).FirstOrDefaultAsync(ct) ?? throw new NotFoundException("Purchase order");

    private static void Validate(PoImportItem i)
    {
        if (string.IsNullOrWhiteSpace(i.PoNumber) || i.PoNumber.Trim().Length > 100) throw new ValidationFailedException("invalid_po_number", "A purchase order number is required (max 100 characters).");
        if (i.Version < 1) throw new ValidationFailedException("invalid_version", $"{i.PoNumber}: the version must be 1 or higher.");
        if (!System.Text.RegularExpressions.Regex.IsMatch(i.CurrencyCode ?? "", "^[A-Z]{3}$")) throw new ValidationFailedException("invalid_currency", $"{i.PoNumber}: the currency must be an ISO 4217 code.");
        if (!PurchaseOrderStatus.All.Contains(i.Status)) throw new ValidationFailedException("invalid_status", $"{i.PoNumber}: the status must be Open, Closed or Cancelled.");
        if (i.PreviouslyInvoicedValue < 0) throw new ValidationFailedException("invalid_amount", $"{i.PoNumber}: amounts cannot be negative.");
        if (i.Lines.Count == 0 || i.Lines.Count > MaxLines) throw new ValidationFailedException("invalid_lines", $"{i.PoNumber}: between 1 and {MaxLines} lines are required.");
        if (i.Lines.Select(l => l.LineNo?.Trim()).Distinct().Count() != i.Lines.Count) throw new ValidationFailedException("duplicate_line", $"{i.PoNumber}: line numbers must be unique.");
        foreach (var l in i.Lines)
        {
            if (string.IsNullOrWhiteSpace(l.LineNo) || l.LineNo.Trim().Length > 30) throw new ValidationFailedException("invalid_line", $"{i.PoNumber}: every line needs a line number (max 30 characters).");
            if (string.IsNullOrWhiteSpace(l.Description) || l.Description.Trim().Length > 500) throw new ValidationFailedException("invalid_line", $"{i.PoNumber}: every line needs a description (max 500 characters).");
            if (l.OrderedQuantity < 0 || l.UnitPrice < 0 || l.PreviouslyInvoicedQuantity < 0) throw new ValidationFailedException("invalid_amount", $"{i.PoNumber}: amounts cannot be negative.");
        }
    }

    private static string Hash(Guid vendorId, PoImportItem i)
    {
        var sb = new StringBuilder();
        sb.Append(vendorId).Append('|').Append(i.CurrencyCode).Append('|').Append(i.Status).Append('|').Append(i.OrderDate.ToString("yyyy-MM-dd")).Append('|').Append(i.PreviouslyInvoicedValue.ToString("F4", System.Globalization.CultureInfo.InvariantCulture));
        foreach (var l in i.Lines.OrderBy(l => l.LineNo.Trim(), StringComparer.Ordinal))
        {
            sb.Append('#').Append(l.LineNo.Trim()).Append('|').Append(l.Description.Trim()).Append('|').Append(l.OrderedQuantity.ToString("F6", System.Globalization.CultureInfo.InvariantCulture))
                .Append('|').Append(l.UnitPrice.ToString("F6", System.Globalization.CultureInfo.InvariantCulture)).Append('|').Append(l.PreviouslyInvoicedQuantity.ToString("F6", System.Globalization.CultureInfo.InvariantCulture));
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    private static PurchaseOrderDto Map(PurchaseOrder p) => new(p.Id, p.LegalEntityId, p.VendorId, p.PoNumber, p.Version, p.CurrencyCode, p.Status, p.OrderDate, p.PreviouslyInvoicedValue,
        p.Lines.OrderBy(l => l.LineNo, StringComparer.Ordinal).Select(l => new PurchaseOrderLineDto(l.LineNo, l.Description, l.OrderedQuantity, l.UnitPrice, l.PreviouslyInvoicedQuantity)).ToList());
}
