using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vpms.BuildingBlocks.Audit;
using Vpms.BuildingBlocks.Errors;
using Vpms.BuildingBlocks.Security;
using Vpms.CustomerAdmin.Contracts;
using Vpms.Vendors.Application;
using Vpms.Vendors.Domain;

namespace Vpms.Vendors.Infrastructure;

public sealed class VendorEntityLinkService(
    VendorsDbContext db, ILegalEntityLookup entities, IAuditPort audit, ICurrentUser current, TimeProvider clock) : IVendorEntityLinkService
{
    private static readonly System.Text.RegularExpressions.Regex Currency = new("^[A-Z]{3}$", System.Text.RegularExpressions.RegexOptions.Compiled);

    public async Task<IReadOnlyList<VendorEntityLinkDto>> ListAsync(Guid vendorId, IReadOnlySet<Guid>? entityScope, CancellationToken ct = default)
    {
        var q = db.EntityLinks.AsNoTracking().Where(l => l.VendorId == vendorId);
        if (entityScope is not null) q = q.Where(l => entityScope.Contains(l.LegalEntityId));
        return (await q.OrderBy(l => l.LegalEntityId).ToListAsync(ct)).Select(Map).ToList();
    }

    public async Task<VendorEntityLinkDto> SaveAsync(Guid vendorId, Guid legalEntityId, SaveVendorEntityLink r, CancellationToken ct = default)
    {
        if (!Currency.IsMatch(r.DefaultCurrency ?? ""))
        {
            throw new ValidationFailedException("invalid_currency", "The currency must be an ISO 4217 code, e.g. SAR.");
        }

        if (r.DefaultDimensionsJson is not null)
        {
            try
            {
                using var doc = JsonDocument.Parse(r.DefaultDimensionsJson);
                if (doc.RootElement.ValueKind != JsonValueKind.Object || r.DefaultDimensionsJson.Length > 4000) throw new JsonException();
            }
            catch (JsonException)
            {
                throw new ValidationFailedException("invalid_json", "The default dimensions must be a small JSON object.");
            }
        }

        if (!await db.Vendors.AnyAsync(v => v.Id == vendorId, ct)) throw new NotFoundException("Vendor");
        if (!await entities.ExistsAsync(legalEntityId, ct)) throw new ValidationFailedException("unknown_legal_entity", "The legal entity does not exist.");

        var link = await db.EntityLinks.FirstOrDefaultAsync(l => l.VendorId == vendorId && l.LegalEntityId == legalEntityId, ct);
        var creating = link is null;
        if (creating)
        {
            link = new VendorEntityLink { Id = Guid.NewGuid(), VendorId = vendorId, LegalEntityId = legalEntityId, CreatedBy = current.UserId, CreatedAt = clock.GetUtcNow() };
            db.EntityLinks.Add(link);
        }

        link!.ErpVendorAccount = Trim(r.ErpVendorAccount, 50);
        link.PaymentTermsCode = Trim(r.PaymentTermsCode, 30);
        link.DefaultCurrency = r.DefaultCurrency!;
        link.DefaultTaxCode = Trim(r.DefaultTaxCode, 30);
        link.DefaultDimensionsJson = r.DefaultDimensionsJson;
        link.WithholdingRuleCode = Trim(r.WithholdingRuleCode, 30);

        audit.Record(new AuditEntry(creating ? "vendor.entity-link.created" : "vendor.entity-link.updated", "VendorEntityLink", link.Id.ToString(), legalEntityId,
            JsonSerializer.Serialize(new { vendorId, link.DefaultCurrency, link.PaymentTermsCode, link.DefaultTaxCode, link.WithholdingRuleCode })));
        await db.SaveChangesAsync(ct);
        return Map(link);
    }

    public async Task<VendorEntityLinkDto> SetBlockedAsync(Guid vendorId, Guid legalEntityId, bool blocked, CancellationToken ct = default)
    {
        var link = await db.EntityLinks.FirstOrDefaultAsync(l => l.VendorId == vendorId && l.LegalEntityId == legalEntityId, ct) ?? throw new NotFoundException("Vendor link");
        var status = blocked ? "Blocked" : "Active";
        if (link.Status != status)
        {
            link.Status = status;
            audit.Record(new AuditEntry(blocked ? "vendor.entity-link.blocked" : "vendor.entity-link.unblocked", "VendorEntityLink", link.Id.ToString(), legalEntityId,
                JsonSerializer.Serialize(new { vendorId })));
            await db.SaveChangesAsync(ct);
        }

        return Map(link);
    }

    internal static VendorEntityLinkDto Map(VendorEntityLink l) => new(l.Id, l.LegalEntityId, l.VendorId, l.ErpVendorAccount, l.PaymentTermsCode,
        l.DefaultCurrency, l.DefaultTaxCode, l.DefaultDimensionsJson, l.WithholdingRuleCode, l.Status);

    private static string? Trim(string? v, int max) => string.IsNullOrWhiteSpace(v) ? null : v.Trim()[..Math.Min(v.Trim().Length, max)];
}
