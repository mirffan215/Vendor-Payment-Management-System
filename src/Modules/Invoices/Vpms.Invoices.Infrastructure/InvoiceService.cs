using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Vpms.BuildingBlocks.Audit;
using Vpms.BuildingBlocks.Errors;
using Vpms.BuildingBlocks.Security;
using Vpms.BuildingBlocks.Security.Encryption;
using Vpms.CustomerAdmin.Contracts;
using Vpms.Documents.Contracts;
using Vpms.Invoices.Application;
using Vpms.Invoices.Domain;
using Vpms.Vendors.Contracts;

namespace Vpms.Invoices.Infrastructure;

public sealed partial class InvoiceService(
    InvoicesDbContext db, IFieldProtector protector, IVendorLookup vendors, ILegalEntityLookup entities, IAttachmentService attachments,
    IAuditPort audit, ICurrentUser current, TimeProvider clock) : IInvoiceService
{
    public const string DuplicateKeyColumn = "invoice.Invoice.DuplicateKey";
    public const string NumberKeyColumn = "invoice.Invoice.NumberKey";
    public const int MaxLines = 500;

    [GeneratedRegex("^[A-Z]{3}$")]
    private static partial Regex CurrencyPattern();

    public async Task<IReadOnlyList<InvoiceListItem>> ListAsync(InvoiceFilter f, IReadOnlySet<Guid>? entityScope, int skip, int take, CancellationToken ct = default)
    {
        var q = db.Invoices.AsNoTracking().AsQueryable();
        if (entityScope is not null) q = q.Where(i => entityScope.Contains(i.LegalEntityId));
        if (!string.IsNullOrWhiteSpace(f.Status)) q = q.Where(i => i.Status == f.Status);
        if (f.VendorId is { } v) q = q.Where(i => i.VendorId == v);
        if (f.LegalEntityId is { } e) q = q.Where(i => i.LegalEntityId == e);
        if (!string.IsNullOrWhiteSpace(f.Source)) q = q.Where(i => i.Source == f.Source);
        if (!string.IsNullOrWhiteSpace(f.Search))
        {
            var term = f.Search.Trim();
            q = q.Where(i => i.InvoiceNumber.Contains(term) || (i.PoReference != null && i.PoReference.Contains(term)));
        }

        var rows = await q.OrderByDescending(i => i.CreatedAt).ThenBy(i => i.Id).Skip(Math.Max(0, skip)).Take(Math.Clamp(take, 1, 200)).ToListAsync(ct);
        var names = await VendorNames(rows.Select(r => r.VendorId), ct);
        return rows.Select(i => new InvoiceListItem(i.Id, i.LegalEntityId, i.VendorId, names.GetValueOrDefault(i.VendorId, ""), i.InvoiceNumber, i.InvoiceDate,
            i.CurrencyCode, i.Total, i.Source, i.Status, i.CreatedAt)).ToList();
    }

    public async Task<InvoiceDto> GetAsync(Guid id, CancellationToken ct = default) => await MapAsync(await LoadAsync(id, track: false, ct), ct);

    public async Task<Guid> EntityOfAsync(Guid id, CancellationToken ct = default) =>
        await db.Invoices.AsNoTracking().Where(i => i.Id == id).Select(i => (Guid?)i.LegalEntityId).FirstOrDefaultAsync(ct) ?? throw new NotFoundException("Invoice");

    public async Task<InvoiceDto> CreateAsync(CreateInvoice r, CancellationToken ct = default) =>
        await CreateCoreAsync(r, InvoiceSource.Manual, current.UserId, ActorKind.Staff, ct);

    /// <summary>Shared by all intake channels. The channel decides source and creator kind; the rules are the same.</summary>
    public async Task<InvoiceDto> CreateCoreAsync(CreateInvoice r, string source, Guid createdBy, string createdByKind, CancellationToken ct)
    {
        Validate(r.Data);
        if (!InvoiceSource.All.Contains(source)) throw new ValidationFailedException("invalid_source", "Unknown invoice source.");
        if (!await entities.ExistsAsync(r.LegalEntityId, ct)) throw new ValidationFailedException("unknown_legal_entity", "The legal entity does not exist.");
        if (await vendors.FindAsync(r.VendorId, ct) is null) throw new NotFoundException("Vendor");
        if (!await vendors.CanTransactAsync(r.VendorId, r.LegalEntityId, ct))
        {
            throw new ConflictException("vendor_cannot_transact", "The vendor is not active or is not linked to this legal entity.");
        }

        var invoice = new Invoice
        {
            Id = Guid.NewGuid(), LegalEntityId = r.LegalEntityId, VendorId = r.VendorId, Source = source, CreatedBy = createdBy,
            CreatedByKind = createdByKind, CreatedAt = clock.GetUtcNow(),
        };
        await ApplyAsync(invoice, r.Data, ct);
        db.Invoices.Add(invoice); // a new graph: the lines are added with it
        audit.Record(new AuditEntry("invoice.created", "Invoice", invoice.Id.ToString(), ActorId: createdBy,
            ChangeSummaryJson: JsonSerializer.Serialize(new { invoice.LegalEntityId, invoice.VendorId, source, invoice.CurrencyCode, invoice.Total })));
        await db.SaveChangesAsync(ct);
        return await GetAsync(invoice.Id, ct);
    }

    public async Task<InvoiceDto> UpdateAsync(Guid id, InvoiceData data, CancellationToken ct = default)
    {
        Validate(data);
        var invoice = await LoadAsync(id, track: true, ct);
        invoice.BeginEdit(current.UserId, clock.GetUtcNow());
        db.Lines.RemoveRange(invoice.Lines);
        await ApplyAsync(invoice, data, ct);
        db.Lines.AddRange(invoice.Lines);
        audit.Record(new AuditEntry("invoice.updated", "Invoice", id.ToString(),
            ChangeSummaryJson: JsonSerializer.Serialize(new { invoice.Version, invoice.CurrencyCode, invoice.Total })));
        await db.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    public Task<InvoiceDto> SubmitAsync(Guid id, CancellationToken ct = default) =>
        Transition(id, "invoice.submitted", (i, now) => i.Submit(now), ct);

    public Task<InvoiceDto> CancelAsync(Guid id, string reason, CancellationToken ct = default) =>
        Transition(id, "invoice.cancelled", (i, _) => i.Cancel(reason), ct, reason);

    public Task<InvoiceDto> RejectAsync(Guid id, string reason, CancellationToken ct = default) =>
        Transition(id, "invoice.rejected", (i, _) => i.Reject(reason), ct, reason);

    // --- internals -----------------------------------------------------------------------------------------

    private async Task<InvoiceDto> Transition(Guid id, string action, Action<Invoice, DateTimeOffset> change, CancellationToken ct, string? reason = null)
    {
        var invoice = await LoadAsync(id, track: true, ct);
        var from = invoice.Status;
        change(invoice, clock.GetUtcNow());
        audit.Record(new AuditEntry(action, "Invoice", id.ToString(),
            ChangeSummaryJson: JsonSerializer.Serialize(new { from, to = invoice.Status, reason })));
        await db.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    private async Task<Invoice> LoadAsync(Guid id, bool track, CancellationToken ct)
    {
        var q = track ? db.Invoices.AsQueryable() : db.Invoices.AsNoTracking();
        return await q.Include(i => i.Lines).FirstOrDefaultAsync(i => i.Id == id, ct) ?? throw new NotFoundException("Invoice");
    }

    private async Task ApplyAsync(Invoice invoice, InvoiceData d, CancellationToken ct)
    {
        invoice.InvoiceNumber = d.InvoiceNumber.Trim();
        invoice.InvoiceDate = d.InvoiceDate;
        invoice.DueDate = d.DueDate;
        invoice.CurrencyCode = d.CurrencyCode;
        invoice.Subtotal = d.Subtotal;
        invoice.TaxTotal = d.TaxTotal;
        invoice.Total = d.Total;
        invoice.PoReference = string.IsNullOrWhiteSpace(d.PoReference) ? null : d.PoReference.Trim();
        invoice.SupplierTaxInvoiceRef = string.IsNullOrWhiteSpace(d.SupplierTaxInvoiceRef) ? null : d.SupplierTaxInvoiceRef.Trim();
        invoice.NumberKeyIndex = await protector.BlindIndexAsync(NumberKeyColumn, $"{invoice.VendorId:N}|{InvoiceNumber.Canonical(invoice.InvoiceNumber)}", ct);
        invoice.DuplicateKeyIndex = await protector.BlindIndexAsync(DuplicateKeyColumn,
            $"{invoice.VendorId:N}|{InvoiceNumber.Canonical(invoice.InvoiceNumber)}|{Money.Round(invoice.Total, invoice.CurrencyCode):F4}|{invoice.InvoiceDate:yyyy-MM-dd}", ct);

        var lineNo = 0;
        invoice.Lines = d.Lines.Select(l => new InvoiceLine
        {
            Id = Guid.NewGuid(), TenantId = invoice.TenantId, VendorId = invoice.VendorId, InvoiceId = invoice.Id, LineNo = ++lineNo,
            Description = l.Description.Trim(), Quantity = l.Quantity, UnitPrice = l.UnitPrice, LineAmount = l.LineAmount,
            TaxCode = string.IsNullOrWhiteSpace(l.TaxCode) ? null : l.TaxCode.Trim(), TaxRate = l.TaxRate, TaxAmount = l.TaxAmount,
            PoLineRef = string.IsNullOrWhiteSpace(l.PoLineRef) ? null : l.PoLineRef.Trim(), DimensionsJson = l.DimensionsJson,
        }).ToList();
    }

    private async Task<InvoiceDto> MapAsync(Invoice i, CancellationToken ct)
    {
        var names = await VendorNames([i.VendorId], ct);
        var links = await db.Attachments.AsNoTracking().Where(a => a.InvoiceId == i.Id).OrderBy(a => a.CreatedAt).ToListAsync(ct);
        var files = new List<InvoiceAttachmentDto>();
        foreach (var a in links)
        {
            var info = await attachments.GetAsync(a.AttachmentId, ct);
            files.Add(new InvoiceAttachmentDto(a.Id, a.AttachmentId, a.Role, info.FileName, info.ScanStatus, info.SizeBytes, a.CreatedAt));
        }

        return new InvoiceDto(i.Id, i.LegalEntityId, i.VendorId, names.GetValueOrDefault(i.VendorId, ""), i.InvoiceNumber, i.InvoiceDate, i.DueDate, i.CurrencyCode,
            i.Subtotal, i.TaxTotal, i.Total, i.PoReference, i.SupplierTaxInvoiceRef, i.Source, i.Status, i.StatusReason, i.Version, i.CreatedAt, i.ReceivedAt,
            i.Lines.OrderBy(l => l.LineNo).Select(l => new InvoiceLineDto(l.Id, l.LineNo, l.Description, l.Quantity, l.UnitPrice, l.LineAmount, l.TaxCode, l.TaxRate,
                l.TaxAmount, l.PoLineRef, l.DimensionsJson)).ToList(), files);
    }

    private async Task<Dictionary<Guid, string>> VendorNames(IEnumerable<Guid> ids, CancellationToken ct)
    {
        var result = new Dictionary<Guid, string>();
        foreach (var id in ids.Distinct())
        {
            result[id] = (await vendors.FindAsync(id, ct))?.LegalName ?? "";
        }

        return result;
    }

    /// <summary>Shape checks only. Business rules (duplicates, totals, tax, PO) belong to the verification engine.</summary>
    public static void Validate(InvoiceData d)
    {
        if (string.IsNullOrWhiteSpace(d.InvoiceNumber) || d.InvoiceNumber.Trim().Length > 100) throw new ValidationFailedException("invalid_invoice_number", "An invoice number is required (max 100 characters).");
        if (InvoiceNumber.Normalize(d.InvoiceNumber).Length == 0) throw new ValidationFailedException("invalid_invoice_number", "The invoice number must contain letters or digits.");
        if (!CurrencyPattern().IsMatch(d.CurrencyCode ?? "")) throw new ValidationFailedException("invalid_currency", "The currency must be an ISO 4217 code, e.g. SAR.");
        if (d.InvoiceDate.Year is < 2000 or > 2100) throw new ValidationFailedException("invalid_date", "The invoice date is not valid.");
        if (d.DueDate is { } due && due.Year is < 2000 or > 2100) throw new ValidationFailedException("invalid_date", "The due date is not valid.");
        if (d.Subtotal < 0 || d.TaxTotal < 0 || d.Total < 0) throw new ValidationFailedException("invalid_amount", "Amounts cannot be negative.");
        if (d.PoReference is { Length: > 100 }) throw new ValidationFailedException("invalid_po_reference", "The PO reference is too long.");
        if (d.SupplierTaxInvoiceRef is { Length: > 200 }) throw new ValidationFailedException("invalid_reference", "The supplier tax-invoice reference is too long.");
        if (d.Lines.Count > MaxLines) throw new ValidationFailedException("too_many_lines", $"An invoice can have at most {MaxLines} lines.");

        foreach (var l in d.Lines)
        {
            if (string.IsNullOrWhiteSpace(l.Description) || l.Description.Trim().Length > 500) throw new ValidationFailedException("invalid_line", "Every line needs a description (max 500 characters).");
            if (l.Quantity < 0 || l.UnitPrice < 0 || l.LineAmount < 0 || l.TaxAmount < 0 || l.TaxRate is < 0 or > 1) throw new ValidationFailedException("invalid_amount", "Line amounts cannot be negative and the tax rate must be between 0 and 1.");
            if (l.DimensionsJson is not null)
            {
                try
                {
                    using var doc = JsonDocument.Parse(l.DimensionsJson);
                    if (doc.RootElement.ValueKind != JsonValueKind.Object || l.DimensionsJson.Length > 2000) throw new JsonException();
                }
                catch (JsonException)
                {
                    throw new ValidationFailedException("invalid_json", "Line dimensions must be a small JSON object.");
                }
            }
        }
    }
}

public static class ActorKind
{
    public const string Staff = "Staff";
    public const string Vendor = "Vendor";
    public const string System = "System";
}
