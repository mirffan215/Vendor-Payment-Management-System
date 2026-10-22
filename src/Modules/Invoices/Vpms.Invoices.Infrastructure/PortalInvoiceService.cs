using Microsoft.EntityFrameworkCore;
using Vpms.BuildingBlocks.Errors;
using Vpms.BuildingBlocks.Security;
using Vpms.BuildingBlocks.Tenancy;
using Vpms.CustomerAdmin.Contracts;
using Vpms.Documents.Contracts;
using Vpms.Invoices.Application;
using Vpms.Invoices.Domain;

namespace Vpms.Invoices.Infrastructure;

/// <summary>What a vendor can do with invoices. No method takes a vendor id; the data layer also filters to the session vendor.</summary>
public sealed class PortalInvoiceService(
    InvoicesDbContext db, InvoiceService invoices, InvoiceAttachmentService invoiceFiles, IAttachmentService attachments, ILegalEntityLookup entities,
    VendorContext vendor, ICurrentUser current) : IPortalInvoiceService
{
    public async Task<IReadOnlyList<PortalInvoiceDto>> ListAsync(CancellationToken ct = default)
    {
        var rows = await db.Invoices.AsNoTracking().OrderByDescending(i => i.CreatedAt).ThenBy(i => i.Id).Take(200).ToListAsync(ct);
        return await MapAsync(rows, ct);
    }

    public async Task<PortalInvoiceDto> GetAsync(Guid id, CancellationToken ct = default)
    {
        var row = await db.Invoices.AsNoTracking().FirstOrDefaultAsync(i => i.Id == id, ct) ?? throw new NotFoundException("Invoice");
        return (await MapAsync([row], ct)).Single();
    }

    public async Task<PortalInvoiceDto> SubmitAsync(PortalSubmitInvoice request, string fileName, Stream content, CancellationToken ct = default)
    {
        var vendorId = vendor.RequireVendorId();
        var created = await invoices.CreateCoreAsync(new CreateInvoice(request.LegalEntityId, vendorId, request.Data), InvoiceSource.Portal, current.UserId, ActorKind.Vendor, ct);
        try
        {
            var info = await attachments.UploadAsync(new UploadRequest(InvoiceAttachmentService.OwnerType, created.Id.ToString(), request.LegalEntityId, fileName, content, current.UserId, UploaderKind.Vendor), ct);
            await invoiceFiles.AddCoreAsync(created.Id, AttachmentRole.Original, info.Id, current.UserId, ActorKind.Vendor, ct);
            await invoices.SubmitAsync(created.Id, ct);
        }
        catch
        {
            await invoices.CancelAsync(created.Id, "The submission was incomplete.", CancellationToken.None);
            throw;
        }

        return await GetAsync(created.Id, ct);
    }

    private async Task<IReadOnlyList<PortalInvoiceDto>> MapAsync(List<Invoice> rows, CancellationToken ct)
    {
        var names = await entities.GetNamesAsync(rows.Select(r => r.LegalEntityId).Distinct(), ct);
        return rows.Select(i => new PortalInvoiceDto(i.Id, i.InvoiceNumber, i.InvoiceDate, i.DueDate, i.CurrencyCode, i.Total, i.PoReference,
            PortalInvoiceStatus.From(i.Status), names.GetValueOrDefault(i.LegalEntityId), i.LegalEntityId, i.CreatedAt)).ToList();
    }
}
