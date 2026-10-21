using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vpms.BuildingBlocks.Audit;
using Vpms.BuildingBlocks.Errors;
using Vpms.BuildingBlocks.Security;
using Vpms.Documents.Contracts;
using Vpms.Vendors.Application;
using Vpms.Vendors.Domain;

namespace Vpms.Vendors.Infrastructure;

public sealed class VendorDocumentService(
    VendorsDbContext db, IAttachmentService attachments, IAuditPort audit, ICurrentUser current, TimeProvider clock) : IVendorDocumentService
{
    public const string OwnerType = "Vendor";

    public async Task<VendorDocumentDto> AddAsync(Guid vendorId, string type, Guid attachmentId, DateOnly? expiresOn, CancellationToken ct = default)
    {
        if (!VendorDocumentType.All.Contains(type))
        {
            throw new ValidationFailedException("invalid_document_type", "Unknown document type.");
        }

        if (!await db.Vendors.AnyAsync(v => v.Id == vendorId, ct))
        {
            throw new NotFoundException("Vendor");
        }

        var info = await attachments.GetAsync(attachmentId, ct);
        if (info.OwnerType != OwnerType || info.OwnerId != vendorId.ToString())
        {
            throw new ValidationFailedException("attachment_mismatch", "The file was not uploaded for this vendor.");
        }

        if (await db.Documents.AnyAsync(d => d.AttachmentId == attachmentId, ct))
        {
            throw new ConflictException("document_exists", "This file is already attached to a document.");
        }

        var doc = new VendorDocument
        {
            Id = Guid.NewGuid(), VendorId = vendorId, Type = type, AttachmentId = attachmentId, ExpiresOn = expiresOn,
            UploadedBy = current.UserId, UploaderKind = UploaderKind.Staff, CreatedAt = clock.GetUtcNow(),
        };
        db.Documents.Add(doc);
        audit.Record(new AuditEntry("vendor.document.added", "VendorDocument", doc.Id.ToString(),
            ChangeSummaryJson: JsonSerializer.Serialize(new { vendorId, type, attachmentId })));
        await db.SaveChangesAsync(ct);
        return (await ListInternalAsync(db, attachments, vendorId, ct)).Single(d => d.Id == doc.Id);
    }

    public Task<IReadOnlyList<VendorDocumentDto>> ListAsync(Guid vendorId, CancellationToken ct = default) =>
        ListInternalAsync(db, attachments, vendorId, ct);

    public async Task<VendorDocumentDto> VerifyAsync(Guid documentId, CancellationToken ct = default)
    {
        var doc = await db.Documents.FirstOrDefaultAsync(d => d.Id == documentId, ct) ?? throw new NotFoundException("Document");
        var info = await attachments.GetAsync(doc.AttachmentId, ct);
        if (info.ScanStatus != ScanStatus.Clean)
        {
            throw new ConflictException("document_not_clean", "The file has not been scanned clean yet.");
        }

        doc.Status = DocumentStatus.Verified;
        doc.VerifiedBy = current.UserId;
        doc.VerifiedAt = clock.GetUtcNow();
        doc.ReviewNote = null;
        audit.Record(new AuditEntry("vendor.document.verified", "VendorDocument", documentId.ToString(), ChangeSummaryJson: JsonSerializer.Serialize(new { doc.VendorId, doc.Type })));
        await db.SaveChangesAsync(ct);
        return (await ListInternalAsync(db, attachments, doc.VendorId, ct)).Single(d => d.Id == documentId);
    }

    public async Task<VendorDocumentDto> RejectAsync(Guid documentId, string note, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(note) || note.Length > 500)
        {
            throw new ValidationFailedException("reason_required", "A reason is required (max 500 characters).");
        }

        var doc = await db.Documents.FirstOrDefaultAsync(d => d.Id == documentId, ct) ?? throw new NotFoundException("Document");
        doc.Status = DocumentStatus.Rejected;
        doc.ReviewNote = note.Trim();
        doc.VerifiedBy = current.UserId;
        doc.VerifiedAt = clock.GetUtcNow();
        audit.Record(new AuditEntry("vendor.document.rejected", "VendorDocument", documentId.ToString(), ChangeSummaryJson: JsonSerializer.Serialize(new { doc.VendorId, doc.Type })));
        await db.SaveChangesAsync(ct);
        return (await ListInternalAsync(db, attachments, doc.VendorId, ct)).Single(d => d.Id == documentId);
    }

    internal static async Task<IReadOnlyList<VendorDocumentDto>> ListInternalAsync(VendorsDbContext db, IAttachmentService attachments, Guid vendorId, CancellationToken ct)
    {
        var docs = await db.Documents.AsNoTracking().Where(d => d.VendorId == vendorId).OrderBy(d => d.CreatedAt).ToListAsync(ct);
        var result = new List<VendorDocumentDto>();
        foreach (var d in docs)
        {
            var info = await attachments.GetAsync(d.AttachmentId, ct);
            result.Add(new VendorDocumentDto(d.Id, d.Type, d.AttachmentId, d.ExpiresOn, d.Status, d.ReviewNote, info.FileName, info.ScanStatus, d.CreatedAt));
        }

        return result;
    }
}
