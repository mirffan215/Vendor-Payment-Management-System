using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vpms.BuildingBlocks.Audit;
using Vpms.BuildingBlocks.Errors;
using Vpms.BuildingBlocks.Security.Encryption;
using Vpms.Vendors.Contracts;
using Vpms.Vendors.Domain;

namespace Vpms.Vendors.Infrastructure;

public sealed class VendorErpSource(VendorsDbContext db, IFieldProtector protector, IAuditPort audit) : IVendorErpSource
{
    public async Task<VendorErpData?> GetAsync(Guid vendorId, Guid legalEntityId, CancellationToken ct = default)
    {
        var row = await Query(legalEntityId).Where(x => x.Vendor.Id == vendorId).Select(x => new Row(x.Vendor, x.Link)).FirstOrDefaultAsync(ct);
        return row is null ? null : await MapAsync(row.Vendor, row.Link, ct);
    }

    public async Task<IReadOnlyList<VendorErpData>> ListActiveAsync(Guid legalEntityId, int take, CancellationToken ct = default)
    {
        var rows = (await Query(legalEntityId).OrderBy(x => x.Vendor.LegalName).Take(Math.Clamp(take, 1, 1000)).ToListAsync(ct)).Select(x => new Row(x.Vendor, x.Link)).ToList();
        var result = new List<VendorErpData>();
        foreach (var r in rows) result.Add(await MapAsync(r.Vendor, r.Link, ct));
        return result;
    }

    public async Task LinkErpAccountAsync(Guid vendorId, Guid legalEntityId, string erpAccount, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(erpAccount) || erpAccount.Length > 50) throw new ValidationFailedException("erp_account_invalid", "The ERP account is not valid.");
        var link = await db.EntityLinks.FirstOrDefaultAsync(l => l.VendorId == vendorId && l.LegalEntityId == legalEntityId, ct) ?? throw new NotFoundException("Vendor link");
        if (link.ErpVendorAccount == erpAccount) return;
        if (!string.IsNullOrWhiteSpace(link.ErpVendorAccount))
        {
            throw new ConflictException("erp_account_conflict", "The vendor is already linked to a different ERP account.");
        }

        link.ErpVendorAccount = erpAccount;
        audit.Record(new AuditEntry("vendor.erp-account.linked", "Vendor", vendorId.ToString(), legalEntityId, JsonSerializer.Serialize(new { erpAccount })));
        await db.SaveChangesAsync(ct);
    }

    private IQueryable<Pair> Query(Guid legalEntityId) =>
        from v in db.Vendors.AsNoTracking()
        join l in db.EntityLinks.AsNoTracking() on v.Id equals l.VendorId
        where l.LegalEntityId == legalEntityId && l.Status == "Active" && v.Status == VendorStatus.Active
        select new Pair { Vendor = v, Link = l };

    private sealed class Pair
    {
        public Vendor Vendor { get; set; } = null!;
        public VendorEntityLink Link { get; set; } = null!;
    }

    private async Task<VendorErpData> MapAsync(Vendor v, VendorEntityLink l, CancellationToken ct)
    {
        var tax = v.TaxIdCipher is null ? null : await protector.UnprotectAsync(VendorService.TaxColumn, v.TaxIdCipher, ct);
        var text = string.Join('|', v.LegalName, v.CountryCode, tax ?? "", l.DefaultCurrency, l.PaymentTermsCode ?? "");
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant()[..16];
        return new VendorErpData(v.Id, l.LegalEntityId, v.LegalName, v.CountryCode, tax, l.DefaultCurrency, l.PaymentTermsCode, l.ErpVendorAccount, hash);
    }

    private sealed record Row(Vendor Vendor, VendorEntityLink Link);
}
