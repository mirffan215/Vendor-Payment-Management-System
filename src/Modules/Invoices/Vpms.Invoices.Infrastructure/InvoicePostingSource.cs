using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vpms.Documents.Contracts;
using Vpms.Invoices.Contracts;
using Vpms.Invoices.Domain;

namespace Vpms.Invoices.Infrastructure;

public sealed class InvoicePostingSource(InvoicesDbContext db, IPayableInvoiceLookup payable, IAttachmentService attachments) : IInvoicePostingSource
{
    public async Task<PostingInvoice?> GetAsync(Guid invoiceId, CancellationToken ct = default)
    {
        var i = await db.Invoices.AsNoTracking().Include(x => x.Lines).FirstOrDefaultAsync(x => x.Id == invoiceId, ct);
        return i is null ? null : (await MapAsync([i], ct)).Single();
    }

    public async Task<IReadOnlyList<PostingInvoice>> ListApprovedAsync(Guid legalEntityId, DateTimeOffset? approvedFrom, int take, CancellationToken ct = default)
    {
        var rows = await db.Invoices.AsNoTracking().Include(i => i.Lines)
            .Where(i => i.LegalEntityId == legalEntityId && i.Status == InvoiceStatus.Approved && i.ApprovedVersion == i.Version
                        && (approvedFrom == null || i.ApprovedAt >= approvedFrom))
            .OrderBy(i => i.ApprovedAt).Take(Math.Clamp(take, 1, 500)).ToListAsync(ct);
        return await MapAsync(rows, ct);
    }

    private async Task<IReadOnlyList<PostingInvoice>> MapAsync(IReadOnlyList<Invoice> rows, CancellationToken ct)
    {
        var result = new List<PostingInvoice>();
        foreach (var i in rows)
        {
            var header = await payable.GetAsync(i.Id, ct);
            if (header is null) continue;
            var lines = i.Lines.OrderBy(l => l.LineNo).Select(l => new PostingLine(l.LineNo, l.Description, l.Quantity, l.UnitPrice, l.LineAmount, l.TaxCode, l.TaxAmount, Dimensions(l.DimensionsJson))).ToList();
            var links = await db.Attachments.AsNoTracking().Where(a => a.InvoiceId == i.Id).OrderBy(a => a.CreatedAt).ToListAsync(ct);
            var files = new List<PostingAttachment>();
            foreach (var link in links)
            {
                var info = await attachments.GetAsync(link.AttachmentId, ct);
                files.Add(new PostingAttachment(link.AttachmentId, link.Role, info.FileName, info.ContentType, info.ScanStatus, info.SizeBytes));
            }

            result.Add(new PostingInvoice(header, i.PoReference, i.Subtotal, i.TaxTotal, lines, files));
        }

        return result;
    }

    private static IReadOnlyDictionary<string, string> Dimensions(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new Dictionary<string, string>();
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new Dictionary<string, string>();
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>();
        }
    }
}
