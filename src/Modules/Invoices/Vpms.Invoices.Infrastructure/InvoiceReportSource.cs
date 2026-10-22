using Microsoft.EntityFrameworkCore;
using Vpms.Invoices.Contracts;
using Vpms.Invoices.Domain;
using Vpms.Invoices.Domain.Verification;

namespace Vpms.Invoices.Infrastructure;

public sealed class InvoiceReportSource(InvoicesDbContext db) : IInvoiceReportSource
{
    public async Task<IReadOnlyList<OpenPayableRow>> OpenPayablesAsync(Guid? legalEntityId, IReadOnlySet<Guid>? entityScope, int max, CancellationToken ct = default)
    {
        var q = db.Invoices.AsNoTracking().Where(i => i.Status == InvoiceStatus.Approved && i.ApprovedVersion == i.Version);
        if (legalEntityId is { } e) q = q.Where(i => i.LegalEntityId == e);
        if (entityScope is not null) q = q.Where(i => entityScope.Contains(i.LegalEntityId));
        return await q.OrderBy(i => i.ApprovedAt).Take(Math.Clamp(max, 1, 20_000))
            .Select(i => new OpenPayableRow(i.Id, i.LegalEntityId, i.VendorId, i.InvoiceNumber, i.InvoiceDate, i.DueDate, i.CurrencyCode, i.Total, i.ApprovedAt)).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<ExceptionRow>> ExceptionsAsync(Guid? legalEntityId, IReadOnlySet<Guid>? entityScope, DateOnly? from, DateOnly? to, int max, CancellationToken ct = default)
    {
        var q = db.Exceptions.AsNoTracking().AsQueryable();
        if (legalEntityId is { } e) q = q.Where(x => x.LegalEntityId == e);
        if (entityScope is not null) q = q.Where(x => entityScope.Contains(x.LegalEntityId));
        if (from is { } f) { var start = new DateTimeOffset(f.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero); q = q.Where(x => x.CreatedAt >= start); }
        if (to is { } t) { var end = new DateTimeOffset(t.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero); q = q.Where(x => x.CreatedAt < end); }

        var rows = await (from x in q
                          join i in db.Invoices.AsNoTracking() on x.InvoiceId equals i.Id
                          orderby x.CreatedAt descending
                          select new ExceptionRow(x.Id, x.LegalEntityId, x.InvoiceId, i.InvoiceNumber, x.RuleCode, x.Severity, x.Status, x.CreatedAt, x.ResolvedAt, x.Resolution, x.AssignedTo))
            .Take(Math.Clamp(max, 1, 20_000)).ToListAsync(ct);
        return rows;
    }

    public async Task<IReadOnlyList<VatRegisterRow>> VatRegisterAsync(Guid? legalEntityId, IReadOnlySet<Guid>? entityScope, DateOnly from, DateOnly to, int max, CancellationToken ct = default)
    {
        var q = db.Invoices.AsNoTracking().Include(i => i.Lines)
            .Where(i => i.InvoiceDate >= from && i.InvoiceDate <= to && i.Status != InvoiceStatus.Draft && i.Status != InvoiceStatus.Rejected && i.Status != InvoiceStatus.Cancelled);
        if (legalEntityId is { } e) q = q.Where(i => i.LegalEntityId == e);
        if (entityScope is not null) q = q.Where(i => entityScope.Contains(i.LegalEntityId));
        var invoices = await q.OrderBy(i => i.InvoiceDate).ThenBy(i => i.InvoiceNumber).Take(Math.Clamp(max, 1, 20_000)).ToListAsync(ct);

        var ids = invoices.Select(i => i.Id).ToList();
        var waived = (await db.Exceptions.AsNoTracking().Where(x => ids.Contains(x.InvoiceId) && x.Status == ExceptionStatus.Waived).Select(x => x.InvoiceId).Distinct().ToListAsync(ct)).ToHashSet();
        return invoices.Select(i => new VatRegisterRow(
            i.Id, i.LegalEntityId, i.VendorId, i.InvoiceNumber, i.InvoiceDate, i.SupplierTaxNumber, i.SupplierTaxInvoiceRef, i.CurrencyCode, i.Subtotal, i.TaxTotal, i.Total,
            string.Join(' ', i.Lines.Select(l => l.TaxCode).Where(c => !string.IsNullOrWhiteSpace(c)).Distinct().Order()), i.Status, waived.Contains(i.Id))).ToList();
    }
}
