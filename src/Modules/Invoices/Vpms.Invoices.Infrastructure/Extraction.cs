using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vpms.BuildingBlocks.Audit;
using Vpms.BuildingBlocks.Errors;
using Vpms.BuildingBlocks.Tenancy;
using Vpms.Documents.Contracts;
using Vpms.Invoices.Application;
using Vpms.Invoices.Domain;

namespace Vpms.Invoices.Infrastructure;

/// <summary>The default extractor: finds nothing. A real OCR engine replaces it by registering its own IDocumentExtractor.</summary>
public sealed class NoOpDocumentExtractor : IDocumentExtractor
{
    public string Engine => "none";

    public Task<ExtractionResult> ExtractAsync(Stream content, string fileName, string contentType, CancellationToken ct = default) =>
        Task.FromResult(ExtractionResult.Empty(Engine));
}

public sealed class InvoiceExtractionService(
    InvoicesDbContext db, IAttachmentService attachments, IDocumentExtractor extractor, IAuditPort audit, TimeProvider clock)
    : IInvoiceExtractionService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<int> RunPendingAsync(int max = 20, CancellationToken ct = default)
    {
        var candidates = await (from a in db.Attachments.AsNoTracking()
                                join i in db.Invoices.AsNoTracking() on a.InvoiceId equals i.Id
                                where a.Role == AttachmentRole.Original && i.Status == InvoiceStatus.Draft
                                      && !db.Extractions.Any(e => e.AttachmentId == a.AttachmentId)
                                orderby a.CreatedAt
                                select new { a.AttachmentId, a.InvoiceId, a.VendorId }).Take(max).ToListAsync(ct);

        var done = 0;
        foreach (var c in candidates)
        {
            var info = await attachments.GetAsync(c.AttachmentId, ct);
            if (info.ScanStatus != ScanStatus.Clean) continue; // not scanned yet (or never will be): try again next run

            ExtractionResult result;
            await using (var content = (await attachments.OpenDownloadAsync(c.AttachmentId, ct)).Content)
            {
                result = await extractor.ExtractAsync(content, info.FileName, info.ContentType, ct);
            }

            db.Extractions.Add(new InvoiceExtraction
            {
                Id = Guid.NewGuid(), VendorId = c.VendorId, InvoiceId = c.InvoiceId, AttachmentId = c.AttachmentId, Engine = result.Engine,
                ResultJson = JsonSerializer.Serialize(result, Json), CreatedAt = clock.GetUtcNow(),
            });
            audit.Record(new AuditEntry("invoice.extraction.suggested", "Invoice", c.InvoiceId.ToString(), ActorType: AuditActorType.System,
                ChangeSummaryJson: JsonSerializer.Serialize(new { c.AttachmentId, result.Engine, fields = result.Fields.Count, lines = result.Lines.Count })));
            await db.SaveChangesAsync(ct);
            done++;
        }

        return done;
    }

    public async Task<IReadOnlyList<ExtractionDto>> ListAsync(Guid invoiceId, CancellationToken ct = default)
    {
        var rows = await db.Extractions.AsNoTracking().Where(e => e.InvoiceId == invoiceId).OrderBy(e => e.CreatedAt).ToListAsync(ct);
        return rows.Select(e => new ExtractionDto(e.Id, e.InvoiceId, e.AttachmentId, e.Engine, e.Status,
            JsonSerializer.Deserialize<ExtractionResult>(e.ResultJson, Json) ?? ExtractionResult.Empty(e.Engine), e.CreatedAt)).ToList();
    }

    public async Task DismissAsync(Guid extractionId, CancellationToken ct = default)
    {
        var e = await db.Extractions.FirstOrDefaultAsync(x => x.Id == extractionId, ct) ?? throw new NotFoundException("Extraction");
        e.Status = ExtractionStatus.Dismissed;
        audit.Record(new AuditEntry("invoice.extraction.dismissed", "Invoice", e.InvoiceId.ToString()));
        await db.SaveChangesAsync(ct);
    }

}

