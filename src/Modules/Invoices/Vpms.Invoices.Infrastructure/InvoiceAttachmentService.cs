using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vpms.BuildingBlocks.Audit;
using Vpms.BuildingBlocks.Errors;
using Vpms.BuildingBlocks.Security;
using Vpms.Documents.Contracts;
using Vpms.Invoices.Application;
using Vpms.Invoices.Domain;

namespace Vpms.Invoices.Infrastructure;

public sealed class InvoiceAttachmentService(
    InvoicesDbContext db, IAttachmentService attachments, IAuditPort audit, ICurrentUser current, TimeProvider clock) : IInvoiceAttachmentService
{
    public const string OwnerType = "Invoice";

    public async Task<InvoiceAttachmentDto> AddAsync(Guid invoiceId, string role, Guid attachmentId, CancellationToken ct = default) =>
        await AddCoreAsync(invoiceId, role, attachmentId, current.UserId, ActorKind.Staff, ct);

    public async Task<InvoiceAttachmentDto> AddCoreAsync(Guid invoiceId, string role, Guid attachmentId, Guid addedBy, string addedByKind, CancellationToken ct)
    {
        if (!AttachmentRole.All.Contains(role)) throw new ValidationFailedException("invalid_attachment_role", "Unknown attachment role.");
        var invoice = await db.Invoices.AsNoTracking().FirstOrDefaultAsync(i => i.Id == invoiceId, ct) ?? throw new NotFoundException("Invoice");

        var info = await attachments.GetAsync(attachmentId, ct);
        if (info.OwnerType != OwnerType || info.OwnerId != invoiceId.ToString())
        {
            throw new ValidationFailedException("attachment_mismatch", "The file was not uploaded for this invoice.");
        }

        if (await db.Attachments.AnyAsync(a => a.AttachmentId == attachmentId, ct))
        {
            throw new ConflictException("attachment_exists", "This file is already attached to an invoice.");
        }

        var link = new InvoiceAttachment
        {
            Id = Guid.NewGuid(), VendorId = invoice.VendorId, InvoiceId = invoiceId, AttachmentId = attachmentId, Role = role,
            AddedBy = addedBy, AddedByKind = addedByKind, CreatedAt = clock.GetUtcNow(),
        };
        db.Attachments.Add(link);
        audit.Record(new AuditEntry("invoice.attachment.added", "Invoice", invoiceId.ToString(), ActorId: addedBy,
            ChangeSummaryJson: JsonSerializer.Serialize(new { attachmentId, role })));
        await db.SaveChangesAsync(ct);
        return new InvoiceAttachmentDto(link.Id, attachmentId, role, info.FileName, info.ScanStatus, info.SizeBytes, link.CreatedAt);
    }

    public async Task<AttachmentDownload> OpenAsync(Guid invoiceId, Guid invoiceAttachmentId, CancellationToken ct = default)
    {
        // Filtered by tenant and, in a vendor session, by vendor: another vendor's link is indistinguishable from an unknown one.
        var link = await db.Attachments.AsNoTracking().FirstOrDefaultAsync(a => a.Id == invoiceAttachmentId && a.InvoiceId == invoiceId, ct)
                   ?? throw new NotFoundException("Attachment");
        return await attachments.OpenDownloadAsync(link.AttachmentId, ct);
    }
}
