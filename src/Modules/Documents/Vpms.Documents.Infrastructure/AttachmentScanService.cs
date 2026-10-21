using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Vpms.BuildingBlocks.Audit;
using Vpms.BuildingBlocks.Errors;
using Vpms.BuildingBlocks.Storage;
using Vpms.BuildingBlocks.Tenancy;
using Vpms.Documents.Application;
using Vpms.Documents.Contracts;

namespace Vpms.Documents.Infrastructure;

public sealed class AttachmentScanService(
    DocumentsDbContext db,
    IFileStore files,
    IVirusScanner scanner,
    IAuditPort audit,
    TenantContext tenant,
    IOptions<DocumentsOptions> options,
    TimeProvider clock) : IAttachmentScanService
{
    public async Task<IReadOnlyList<PendingScan>> ListPendingAsync(int max, CancellationToken ct = default)
    {
        using var _ = tenant.BeginPlatformScope();
        return await db.Attachments.AsNoTracking()
            .Where(a => a.ScanStatus == ScanStatus.Pending && a.ScanAttempts < options.Value.MaxScanAttempts)
            .OrderBy(a => a.UploadedAt).Take(max)
            .Select(a => new PendingScan(a.TenantId, a.Id)).ToListAsync(ct);
    }

    public async Task ScanAsync(Guid attachmentId, CancellationToken ct = default)
    {
        var attachment = await db.Attachments.FirstOrDefaultAsync(a => a.Id == attachmentId, ct)
                         ?? throw new NotFoundException("Attachment");
        if (attachment.ScanStatus != ScanStatus.Pending)
        {
            return;
        }

        ScanResult result;
        await using (var content = await files.GetAsync(attachment.StorageKey, ct))
        {
            result = content is null
                ? new ScanResult(ScanVerdict.Error, Detail: "stored content is missing")
                : await SafeScanAsync(content, ct);
        }

        attachment.ScannedAt = clock.GetUtcNow();
        switch (result.Verdict)
        {
            case ScanVerdict.Clean:
                attachment.ScanStatus = ScanStatus.Clean;
                Record("attachment.scan.clean", attachment, null);
                break;
            case ScanVerdict.Infected:
                attachment.ScanStatus = ScanStatus.Infected;
                attachment.ScanSignature = result.Signature;
                await files.DeleteAsync(attachment.StorageKey, ct); // destroy the infected content, keep the record
                Record("attachment.scan.infected", attachment, result.Signature);
                break;
            default:
                attachment.ScanAttempts++;
                if (attachment.ScanAttempts >= options.Value.MaxScanAttempts)
                {
                    attachment.ScanStatus = ScanStatus.Failed;
                    Record("attachment.scan.failed", attachment, result.Detail);
                }

                break;
        }

        await db.SaveChangesAsync(ct);
    }

    private async Task<ScanResult> SafeScanAsync(Stream content, CancellationToken ct)
    {
        try
        {
            return await scanner.ScanAsync(content, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ScanResult(ScanVerdict.Error, Detail: ex.GetType().Name);
        }
    }

    private void Record(string action, Domain.Attachment a, string? detail) =>
        audit.Record(new AuditEntry(action, "Attachment", a.Id.ToString(), a.LegalEntityId,
            JsonSerializer.Serialize(new { detail }), ActorType: AuditActorType.System));
}
