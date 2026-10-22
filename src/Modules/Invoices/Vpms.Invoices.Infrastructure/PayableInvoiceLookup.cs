using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Vpms.Invoices.Contracts;
using Vpms.Invoices.Domain;

namespace Vpms.Invoices.Infrastructure;

public sealed class PayableInvoiceLookup(InvoicesDbContext db) : IPayableInvoiceLookup
{
    public async Task<PayableInvoice?> GetAsync(Guid invoiceId, CancellationToken ct = default)
    {
        var i = await db.Invoices.AsNoTracking().FirstOrDefaultAsync(x => x.Id == invoiceId, ct);
        return i is null ? null : (await MapAsync([i], ct)).Single();
    }

    public async Task<IReadOnlyList<PayableInvoice>> ListPayableAsync(Guid? legalEntityId, IReadOnlySet<Guid>? entityScope, int take, CancellationToken ct = default)
    {
        var q = db.Invoices.AsNoTracking().Where(i => i.Status == InvoiceStatus.Approved && i.ApprovedVersion == i.Version);
        if (legalEntityId is { } e) q = q.Where(i => i.LegalEntityId == e);
        if (entityScope is not null) q = q.Where(i => entityScope.Contains(i.LegalEntityId));
        var rows = await q.OrderBy(i => i.ApprovedAt).Take(Math.Clamp(take, 1, 500)).ToListAsync(ct);
        return await MapAsync(rows, ct);
    }

    /// <summary>Changes whenever something a payment depends on changes.</summary>
    internal static string IntegrityHash(Invoice i)
    {
        var text = string.Join('|', i.Id, i.LegalEntityId, i.VendorId, InvoiceNumber.Canonical(i.InvoiceNumber), i.Total.ToString("F4", System.Globalization.CultureInfo.InvariantCulture),
            i.CurrencyCode, i.DueDate?.ToString("yyyy-MM-dd") ?? "", i.Version, i.ApprovedVersion?.ToString() ?? "");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }

    private async Task<IReadOnlyList<PayableInvoice>> MapAsync(IReadOnlyList<Invoice> rows, CancellationToken ct)
    {
        var ids = rows.Select(r => r.Id).ToList();
        var resolvers = await db.Exceptions.AsNoTracking().Where(e => ids.Contains(e.InvoiceId) && e.ResolvedBy != null)
            .Select(e => new { e.InvoiceId, By = e.ResolvedBy!.Value }).ToListAsync(ct);
        return rows.Select(i =>
        {
            var preparers = new[] { i.CreatedByKind == ActorKind.Staff ? i.CreatedBy : (Guid?)null, i.LastEditedBy }.Where(g => g.HasValue).Select(g => g!.Value)
                .Concat(resolvers.Where(r => r.InvoiceId == i.Id).Select(r => r.By)).Distinct().ToList();
            return new PayableInvoice(i.Id, i.LegalEntityId, i.VendorId, i.InvoiceNumber, i.InvoiceDate, i.DueDate, i.CurrencyCode, i.Total, i.Status, i.Version,
                i.ApprovedVersion, i.ApprovedAt, IntegrityHash(i), preparers);
        }).ToList();
    }
}
