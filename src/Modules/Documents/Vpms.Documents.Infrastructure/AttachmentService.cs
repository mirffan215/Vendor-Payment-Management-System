using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Vpms.BuildingBlocks.Audit;
using Vpms.BuildingBlocks.Errors;
using Vpms.BuildingBlocks.Storage;
using Vpms.BuildingBlocks.Tenancy;
using Vpms.Documents.Application;
using Vpms.Documents.Contracts;
using Vpms.Documents.Domain;

namespace Vpms.Documents.Infrastructure;

public sealed class AttachmentService(
    DocumentsDbContext db,
    IFileStore files,
    IAuditPort audit,
    ITenantContext tenant,
    IOptions<DocumentsOptions> options,
    TimeProvider clock) : IAttachmentService
{
    public async Task<AttachmentInfo> UploadAsync(UploadRequest request, CancellationToken ct = default)
    {
        var tenantId = tenant.RequireTenantId();
        var max = options.Value.MaxBytes;

        // Read at most max+1 bytes so an oversize upload is rejected without buffering all of it.
        await using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await request.Content.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > max)
            {
                throw new ValidationFailedException("file_too_large", $"Files may be at most {max / (1024 * 1024)} MB.", 413);
            }

            buffer.Write(chunk, 0, read);
        }

        DetectedFile detected;
        try
        {
            detected = FileInspector.Inspect(request.FileName, buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
        }
        catch (FileRejectedException ex)
        {
            throw new ValidationFailedException(ex.Code, ex.Message, ex.Code == "unsupported_type" ? 415 : 400);
        }

        var id = Guid.NewGuid();
        var sha = Convert.ToHexStringLower(SHA256.HashData(buffer.GetBuffer().AsSpan(0, (int)buffer.Length)));
        buffer.Position = 0;
        var key = await files.PutAsync(request.LegalEntityId, "attachments", id, detected.SafeName, buffer, ct);

        var attachment = new Attachment
        {
            Id = id, TenantId = tenantId, LegalEntityId = request.LegalEntityId, OwnerType = request.OwnerType,
            OwnerId = request.OwnerId, FileName = detected.SafeName, ContentType = detected.ContentType,
            SizeBytes = buffer.Length, Sha256 = sha, StorageKey = key, ScanStatus = ScanStatus.Pending,
            UploadedBy = request.UploadedBy, UploaderKind = request.UploaderKind, UploadedAt = clock.GetUtcNow(),
        };
        db.Attachments.Add(attachment);
        audit.Record(new AuditEntry("attachment.uploaded", "Attachment", id.ToString(), request.LegalEntityId,
            JsonSerializer.Serialize(new { request.OwnerType, request.OwnerId, size = buffer.Length, sha256 = sha, type = detected.ContentType }),
            ActorId: request.UploadedBy));

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch
        {
            await files.DeleteAsync(key, CancellationToken.None); // do not leave an orphan blob behind
            throw;
        }

        return Map(attachment);
    }

    public async Task<AttachmentInfo> ReassignFromIntakeAsync(Guid id, string newOwnerType, string newOwnerId, Guid? legalEntityId, CancellationToken ct = default)
    {
        var attachment = await db.Attachments.FirstOrDefaultAsync(a => a.Id == id, ct) ?? throw new NotFoundException("Attachment");
        if (attachment.OwnerType != IntakeOwnerType)
        {
            throw new ConflictException("attachment_not_movable", "Only files received through intake can be reassigned.");
        }

        var from = attachment.OwnerId;
        attachment.OwnerType = newOwnerType;
        attachment.OwnerId = newOwnerId;
        attachment.LegalEntityId ??= legalEntityId;
        audit.Record(new AuditEntry("attachment.reassigned", "Attachment", id.ToString(), attachment.LegalEntityId,
            JsonSerializer.Serialize(new { fromOwner = from, newOwnerType, newOwnerId })));
        await db.SaveChangesAsync(ct);
        return Map(attachment);
    }

    public const string IntakeOwnerType = "IntakeMessage";

    public async Task<AttachmentInfo> GetAsync(Guid id, CancellationToken ct = default) =>
        Map(await db.Attachments.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, ct)
            ?? throw new NotFoundException("Attachment"));

    public async Task<IReadOnlyList<AttachmentInfo>> ListForOwnerAsync(string ownerType, string ownerId, CancellationToken ct = default) =>
        (await db.Attachments.AsNoTracking().Where(a => a.OwnerType == ownerType && a.OwnerId == ownerId)
            .OrderBy(a => a.UploadedAt).ToListAsync(ct)).Select(Map).ToList();

    public async Task<AttachmentDownload> OpenDownloadAsync(Guid id, CancellationToken ct = default)
    {
        var attachment = await db.Attachments.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, ct)
                         ?? throw new NotFoundException("Attachment");

        if (attachment.ScanStatus != ScanStatus.Clean)
        {
            throw new ConflictException("attachment_not_ready",
                attachment.ScanStatus == ScanStatus.Pending
                    ? "The file is still being scanned."
                    : "The file is not available.");
        }

        var stream = await files.GetAsync(attachment.StorageKey, ct) ?? throw new NotFoundException("Attachment");
        audit.Record(new AuditEntry("attachment.downloaded", "Attachment", id.ToString(), attachment.LegalEntityId));
        await db.SaveChangesAsync(ct); // flushes the audit record in its own transaction
        return new AttachmentDownload(stream, attachment.FileName, attachment.ContentType, attachment.SizeBytes);
    }

    private static AttachmentInfo Map(Attachment a) => new(a.Id, a.OwnerType, a.OwnerId, a.LegalEntityId, a.FileName,
        a.ContentType, a.SizeBytes, a.Sha256, a.ScanStatus, a.UploadedAt);
}
