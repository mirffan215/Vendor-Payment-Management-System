using System.Net.Mail;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vpms.BuildingBlocks.Audit;
using Vpms.BuildingBlocks.Errors;
using Vpms.BuildingBlocks.Security;
using Vpms.BuildingBlocks.Tenancy;
using Vpms.CustomerAdmin.Contracts;
using Vpms.Documents.Contracts;
using Vpms.Vendors.Application;
using Vpms.Vendors.Domain;

namespace Vpms.Vendors.Infrastructure;

public sealed class VendorPortalService(
    VendorsDbContext db, VendorContext vendor, BankDetailsService bank, VendorDocumentService documents, IAttachmentService attachments,
    ILegalEntityLookup entities, IAuditPort audit, ICurrentUser current, TimeProvider clock) : IVendorPortalService
{
    public const int MaxDocuments = 50;

    private Guid VendorId => vendor.RequireVendorId();

    public async Task<PortalProfile> GetProfileAsync(CancellationToken ct = default) => Map(await Load(ct));

    public async Task<PortalProfile> UpdateProfileAsync(UpdatePortalProfile r, CancellationToken ct = default)
    {
        var v = await Load(ct);
        if (v.Status == VendorStatus.Rejected)
        {
            throw new ConflictException("invalid_transition", "This profile can no longer be changed. Please contact the customer.");
        }

        v.LegalNameAr = Optional(r.LegalNameAr, 300, "invalid_name");
        v.TradeName = Optional(r.TradeName, 300, "invalid_name");
        v.ContactPhone = Optional(r.ContactPhone, 50, "invalid_phone");
        var email = Optional(r.ContactEmail, 320, "invalid_email");
        if (email is not null && !(MailAddress.TryCreate(email, out var parsed) && parsed.Address == email))
        {
            throw new ValidationFailedException("invalid_email", "The contact email address is not valid.");
        }

        v.ContactEmail = email;
        audit.Record(new AuditEntry("vendor.profile.updated-by-vendor", "Vendor", v.Id.ToString(), ActorId: current.UserId));
        await db.SaveChangesAsync(ct);
        return Map(v);
    }

    public async Task<IReadOnlyList<PortalEntityLink>> ListEntityLinksAsync(CancellationToken ct = default)
    {
        var links = await db.EntityLinks.AsNoTracking().OrderBy(l => l.CreatedAt).ToListAsync(ct);
        var names = await entities.GetNamesAsync(links.Select(l => l.LegalEntityId).Distinct(), ct);
        return links.Select(l => new PortalEntityLink(l.LegalEntityId, names.GetValueOrDefault(l.LegalEntityId), l.PaymentTermsCode, l.DefaultCurrency, l.Status)).ToList();
    }

    public Task<IReadOnlyList<VendorDocumentDto>> ListDocumentsAsync(CancellationToken ct = default) =>
        VendorDocumentService.ListInternalAsync(db, attachments, VendorId, ct);

    public async Task<VendorDocumentDto> UploadDocumentAsync(string type, DateOnly? expiresOn, string fileName, Stream content, CancellationToken ct = default)
    {
        var vendorId = VendorId;
        if (!VendorDocumentType.All.Contains(type)) throw new ValidationFailedException("invalid_document_type", "Unknown document type.");
        if (await db.Documents.CountAsync(ct) >= MaxDocuments)
        {
            throw new ConflictException("document_limit", "The maximum number of documents has been reached.");
        }

        var info = await attachments.UploadAsync(new UploadRequest(VendorDocumentService.OwnerType, vendorId.ToString(), null, fileName, content, current.UserId, UploaderKind.Vendor), ct);
        return await documents.AddCoreAsync(vendorId, type, info.Id, expiresOn, UploaderKind.Vendor, ct);
    }

    public async Task<AttachmentDownload> OpenDocumentAsync(Guid documentId, CancellationToken ct = default)
    {
        // The vendor filter makes another vendor's document id indistinguishable from an unknown one.
        var doc = await db.Documents.AsNoTracking().FirstOrDefaultAsync(d => d.Id == documentId, ct) ?? throw new NotFoundException("Document");
        return await attachments.OpenDownloadAsync(doc.AttachmentId, ct);
    }

    public Task<IReadOnlyList<BankDetailsDto>> ListBankDetailsAsync(CancellationToken ct = default) => bank.ListAsync(VendorId, ct);

    public async Task<IReadOnlyList<PortalBankChange>> ListBankChangesAsync(CancellationToken ct = default)
    {
        var changes = await bank.ListChangesAsync(VendorId, null, ct);
        return changes.Select(Map).ToList();
    }

    public async Task<PortalBankChange> ProposeBankDetailsAsync(ProposeBankDetails r, CancellationToken ct = default)
    {
        // A vendor can never confirm the shared-account warning; that is a decision for staff.
        var change = await bank.ProposeCoreAsync(VendorId, r with { ConfirmSharedAccount = false }, ChangeSource.VendorPortal, current.UserId, ct);
        return Map(change);
    }

    public async Task<PortalBankChange> CancelBankChangeAsync(Guid changeId, CancellationToken ct = default)
    {
        var change = await db.BankDetailChanges.AsNoTracking().FirstOrDefaultAsync(c => c.Id == changeId, ct) ?? throw new NotFoundException("Bank-detail change");
        if (change.Source != ChangeSource.VendorPortal)
        {
            throw new ConflictException("not_cancellable", "Only requests raised in the portal can be withdrawn here.");
        }

        return Map(await bank.CancelAsync(changeId, ct));
    }

    public async Task<IReadOnlyList<RequestDto>> ListRequestsAsync(CancellationToken ct = default)
    {
        var requests = await db.Requests.AsNoTracking().OrderByDescending(r => r.CreatedAt).ToListAsync(ct);
        var ids = requests.Select(r => r.Id).ToList();
        var messages = await db.Messages.AsNoTracking().Where(m => ids.Contains(m.RequestId)).OrderBy(m => m.CreatedAt).ToListAsync(ct);
        return requests.Select(r => ToDto(r, messages.Where(m => m.RequestId == r.Id))).ToList();
    }

    public async Task<RequestDto> ReplyToRequestAsync(Guid requestId, string body, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(body) || body.Length > 4000) throw new ValidationFailedException("invalid_message", "A message is required (max 4000 characters).");
        var request = await db.Requests.FirstOrDefaultAsync(r => r.Id == requestId, ct) ?? throw new NotFoundException("Request");
        if (request.Status == RequestStatus.Closed) throw new ConflictException("request_closed", "This request is closed.");

        request.Status = RequestStatus.Responded;
        db.Messages.Add(new VendorMessage
        {
            Id = Guid.NewGuid(), VendorId = request.VendorId, RequestId = request.Id, Direction = MessageDirection.FromVendor, Body = body.Trim(),
            AuthorId = current.UserId, AuthorKind = ActorKind.Vendor, CreatedAt = clock.GetUtcNow(),
        });
        audit.Record(new AuditEntry("vendor.request.vendor-replied", "VendorRequest", requestId.ToString(), ActorId: current.UserId));
        await db.SaveChangesAsync(ct);

        var messages = await db.Messages.AsNoTracking().Where(m => m.RequestId == requestId).OrderBy(m => m.CreatedAt).ToListAsync(ct);
        return ToDto(request, messages);
    }

    // --- internals -----------------------------------------------------------------------------------------

    private async Task<Vendor> Load(CancellationToken ct) =>
        await db.Vendors.FirstOrDefaultAsync(v => v.Id == VendorId, ct) ?? throw new NotFoundException("Vendor");

    private static string? Optional(string? value, int max, string code)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        if (trimmed.Length > max) throw new ValidationFailedException(code, $"The value is too long (max {max} characters).");
        return trimmed;
    }

    private static PortalProfile Map(Vendor v) => new(v.Id, v.LegalName, v.LegalNameAr, v.TradeName, v.CountryCode, v.ContactEmail, v.ContactPhone, v.Status, v.VerificationStatus);

    private static PortalBankChange Map(BankDetailChangeDto c) => new(c.Id, c.Status, c.Reason, c.MadeAt, c.ExpiresAt, c.CheckedAt, c.Proposed);

    private static RequestDto ToDto(VendorRequest r, IEnumerable<VendorMessage> messages) => new(r.Id, r.VendorId, r.Subject, r.Status, r.CreatedAt,
        messages.Select(m => new MessageDto(m.Id, m.Direction, m.Body, m.AuthorKind, m.CreatedAt)).ToList());
}
