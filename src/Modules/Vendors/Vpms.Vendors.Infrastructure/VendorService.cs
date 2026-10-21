using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vpms.BuildingBlocks.Audit;
using Vpms.BuildingBlocks.Errors;
using Vpms.BuildingBlocks.Security;
using Vpms.BuildingBlocks.Security.Encryption;
using Vpms.CustomerAdmin.Contracts;
using Vpms.Documents.Contracts;
using Vpms.Vendors.Application;
using Vpms.Vendors.Contracts;
using Vpms.Vendors.Domain;

namespace Vpms.Vendors.Infrastructure;

public sealed class VendorService(
    VendorsDbContext db, IFieldProtector protector, IAuditPort audit, ICurrentUser current, TimeProvider clock,
    IAttachmentService attachments) : IVendorService, IVendorLookup
{
    public const string TaxColumn = "vendor.Vendor.TaxId";
    public const string RegistrationColumn = "vendor.Vendor.RegistrationNo";

    public async Task<IReadOnlyList<VendorListItem>> ListAsync(string? status, string? search, IReadOnlySet<Guid>? entityScope, int skip, int take, CancellationToken ct = default)
    {
        var q = db.Vendors.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(status)) q = q.Where(v => v.Status == status);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            q = q.Where(v => v.LegalName.Contains(term) || (v.LegalNameAr != null && v.LegalNameAr.Contains(term)) || (v.TradeName != null && v.TradeName.Contains(term)));
        }

        if (entityScope is not null)
        {
            q = q.Where(v => db.EntityLinks.Any(l => l.VendorId == v.Id && entityScope.Contains(l.LegalEntityId)));
        }

        return await q.OrderBy(v => v.LegalName).Skip(Math.Max(0, skip)).Take(Math.Clamp(take, 1, 200))
            .Select(v => new VendorListItem(v.Id, v.LegalName, v.CountryCode, v.Status, v.VerificationStatus, v.CreatedAt)).ToListAsync(ct);
    }

    public async Task<VendorDetails> GetAsync(Guid id, CancellationToken ct = default)
    {
        var vendor = await db.Vendors.AsNoTracking().FirstOrDefaultAsync(v => v.Id == id, ct) ?? throw new NotFoundException("Vendor");
        var documents = await VendorDocumentService.ListInternalAsync(db, attachments, id, ct);
        var links = await db.EntityLinks.AsNoTracking().Where(l => l.VendorId == id).OrderBy(l => l.LegalEntityId).ToListAsync(ct);
        return new VendorDetails(await MapAsync(vendor, ct), documents, links.Select(VendorEntityLinkService.Map).ToList());
    }

    public async Task<VendorDto> CreateAsync(CreateVendor r, CancellationToken ct = default)
    {
        var vendor = new Vendor
        {
            Id = Guid.NewGuid(), CreatedBy = current.UserId, CreatedAt = clock.GetUtcNow(), CountryCode = r.CountryCode,
        };
        await ApplyAsync(vendor, r.LegalName, r.LegalNameAr, r.TradeName, r.TaxId, r.RegistrationNo, r.ContactEmail, r.ContactPhone, ct);
        if (!AdminCountry.IsMatch(r.CountryCode ?? ""))
        {
            throw new ValidationFailedException("invalid_country", "The country must be an ISO 3166 alpha-2 code, e.g. SA.");
        }

        if (!r.ConfirmDuplicate && vendor.TaxIdIndex is not null && await db.Vendors.AnyAsync(v => v.TaxIdIndex == vendor.TaxIdIndex, ct))
        {
            throw new ConflictException("vendor_duplicate", "A vendor with this tax number already exists. Confirm to create it anyway.");
        }

        db.Vendors.Add(vendor);
        audit.Record(new AuditEntry("vendor.created", "Vendor", vendor.Id.ToString(),
            ChangeSummaryJson: JsonSerializer.Serialize(new { vendor.LegalName, vendor.CountryCode, duplicateConfirmed = r.ConfirmDuplicate })));
        await db.SaveChangesAsync(ct);
        return await MapAsync(vendor, ct);
    }

    public async Task<VendorDto> UpdateAsync(Guid id, UpdateVendor r, CancellationToken ct = default)
    {
        var vendor = await Load(id, ct);
        if (vendor.Status is VendorStatus.Rejected)
        {
            throw new ConflictException("invalid_transition", "Reopen the vendor before changing it.");
        }

        var changedTax = r.TaxId is not null && AdminValidate(r.TaxId) != await protector.UnprotectAsync(TaxColumn, vendor.TaxIdCipher, ct);
        await ApplyAsync(vendor, r.LegalName, r.LegalNameAr, r.TradeName, r.TaxId, r.RegistrationNo, r.ContactEmail, r.ContactPhone, ct);
        vendor.MarkEdited(current.UserId, clock.GetUtcNow());
        audit.Record(new AuditEntry("vendor.updated", "Vendor", id.ToString(),
            ChangeSummaryJson: JsonSerializer.Serialize(new { vendor.LegalName, taxIdChanged = changedTax, status = vendor.Status })));
        await db.SaveChangesAsync(ct);
        return await MapAsync(vendor, ct);
    }

    public Task<VendorDto> SubmitAsync(Guid id, CancellationToken ct = default) =>
        Transition(id, "vendor.submitted", (v, now) => v.Submit(current.UserId, ActorKind.Staff, now), ct);

    public Task<VendorDto> StartReviewAsync(Guid id, CancellationToken ct = default) =>
        Transition(id, "vendor.review-started", (v, _) => v.StartReview(current.UserId), ct);

    public async Task<VendorDto> ApproveAsync(Guid id, CancellationToken ct = default)
    {
        var missing = await MissingRequiredDocumentsAsync(id, ct);
        if (missing.Count > 0)
        {
            throw new ConflictException("documents_missing",
                $"Required documents are missing, not verified, or not scanned clean: {string.Join(", ", missing)}.");
        }

        return await Transition(id, "vendor.approved", (v, now) => v.Approve(current.UserId, now), ct);
    }

    public Task<VendorDto> RejectAsync(Guid id, string reason, CancellationToken ct = default) =>
        Transition(id, "vendor.rejected", (v, now) => v.Reject(reason, now), ct, reason);

    public Task<VendorDto> ReopenAsync(Guid id, CancellationToken ct = default) =>
        Transition(id, "vendor.reopened", (v, _) => v.Reopen(), ct);

    public Task<VendorDto> SuspendAsync(Guid id, string reason, CancellationToken ct = default) =>
        Transition(id, "vendor.suspended", (v, _) => v.Suspend(current.UserId, reason), ct, reason);

    public Task<VendorDto> ReactivateAsync(Guid id, CancellationToken ct = default) =>
        Transition(id, "vendor.reactivated", (v, _) => v.Reactivate(current.UserId), ct);

    public async Task<VendorSummary?> FindAsync(Guid vendorId, CancellationToken ct = default) =>
        await db.Vendors.AsNoTracking().Where(v => v.Id == vendorId).Select(v => new VendorSummary(v.Id, v.LegalName, v.Status, v.CountryCode)).FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyList<Guid>> FindActiveByContactEmailAsync(string email, Guid legalEntityId, CancellationToken ct = default)
    {
        var normalized = email.Trim().ToLowerInvariant();
        if (normalized.Length == 0) return [];
        return await db.Vendors.AsNoTracking()
            .Where(v => v.Status == VendorStatus.Active && v.ContactEmail != null && v.ContactEmail.ToLower() == normalized
                        && db.EntityLinks.Any(l => l.VendorId == v.Id && l.LegalEntityId == legalEntityId && l.Status == "Active"))
            .Select(v => v.Id).ToListAsync(ct);
    }

    public async Task<Guid?> FindByErpAccountAsync(string erpVendorAccount, Guid legalEntityId, CancellationToken ct = default) =>
        await db.EntityLinks.AsNoTracking().Where(l => l.LegalEntityId == legalEntityId && l.ErpVendorAccount == erpVendorAccount)
            .Select(l => (Guid?)l.VendorId).FirstOrDefaultAsync(ct);

    public async Task<string?> GetLinkCurrencyAsync(Guid vendorId, Guid legalEntityId, CancellationToken ct = default) =>
        await db.EntityLinks.AsNoTracking().Where(l => l.VendorId == vendorId && l.LegalEntityId == legalEntityId)
            .Select(l => l.DefaultCurrency).FirstOrDefaultAsync(ct);

    public async Task<bool> CanTransactAsync(Guid vendorId, Guid legalEntityId, CancellationToken ct = default) =>
        await db.Vendors.AnyAsync(v => v.Id == vendorId && v.Status == VendorStatus.Active, ct)
        && await db.EntityLinks.AnyAsync(l => l.VendorId == vendorId && l.LegalEntityId == legalEntityId && l.Status == "Active", ct);

    // --- internals -----------------------------------------------------------------------------------------

    private async Task<VendorDto> Transition(Guid id, string action, Action<Vendor, DateTimeOffset> change, CancellationToken ct, string? reason = null)
    {
        var vendor = await Load(id, ct);
        var from = vendor.Status;
        try
        {
            change(vendor, clock.GetUtcNow());
        }
        catch (SegregationOfDutiesException ex)
        {
            await audit.WriteNowAsync(new AuditEntry("sod.vendor.blocked", "Vendor", id.ToString(),
                ChangeSummaryJson: JsonSerializer.Serialize(new { attempted = action, rule = ex.Rule })), ct);
            throw;
        }

        audit.Record(new AuditEntry(action, "Vendor", id.ToString(),
            ChangeSummaryJson: JsonSerializer.Serialize(new { from, to = vendor.Status, reason })));
        await db.SaveChangesAsync(ct);
        return await MapAsync(vendor, ct);
    }

    private async Task<List<string>> MissingRequiredDocumentsAsync(Guid vendorId, CancellationToken ct)
    {
        var docs = await VendorDocumentService.ListInternalAsync(db, attachments, vendorId, ct);
        return VendorDocumentType.RequiredForApproval
            .Where(type => !docs.Any(d => d.Type == type && d.Status == DocumentStatus.Verified && d.ScanStatus == ScanStatus.Clean
                                          && (d.ExpiresOn == null || d.ExpiresOn >= DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime))))
            .ToList();
    }

    private async Task<Vendor> Load(Guid id, CancellationToken ct) =>
        await db.Vendors.FirstOrDefaultAsync(v => v.Id == id, ct) ?? throw new NotFoundException("Vendor");

    private static readonly System.Text.RegularExpressions.Regex AdminCountry = new("^[A-Z]{2}$", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static string AdminValidate(string taxId) => VendorValidation.NormalizeId(taxId);

    private async Task ApplyAsync(Vendor v, string? legalName, string? legalNameAr, string? tradeName, string? taxId, string? registrationNo,
        string? email, string? phone, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(legalName) || legalName.Length > 300)
        {
            throw new ValidationFailedException("invalid_name", "The legal name is required (max 300 characters).");
        }

        if (email is not null && (email.Length > 320 || !System.Text.RegularExpressions.Regex.IsMatch(email, @"^[^@\s]+@[^@\s]+\.[^@\s]+$")))
        {
            throw new ValidationFailedException("invalid_email", "Enter a valid email address.");
        }

        if (phone is not null && phone.Length > 50)
        {
            throw new ValidationFailedException("invalid_phone", "The phone number is too long.");
        }

        v.LegalName = legalName.Trim();
        v.LegalNameAr = Trim(legalNameAr);
        v.TradeName = Trim(tradeName);
        v.ContactEmail = Trim(email);
        v.ContactPhone = Trim(phone);

        if (taxId is not null)
        {
            var normalized = VendorValidation.NormalizeId(taxId);
            if (normalized.Length == 0)
            {
                v.TaxIdCipher = null;
                v.TaxIdIndex = null;
            }
            else if (normalized.Length is < 5 or > 30)
            {
                throw new ValidationFailedException("invalid_tax_number", "The tax number looks invalid.");
            }
            else
            {
                v.TaxIdCipher = await protector.ProtectAsync(TaxColumn, normalized, ct);
                v.TaxIdIndex = await protector.BlindIndexAsync(TaxColumn, normalized, ct);
            }
        }

        if (registrationNo is not null)
        {
            var normalized = VendorValidation.NormalizeId(registrationNo);
            v.RegistrationNoCipher = normalized.Length == 0 ? null : await protector.ProtectAsync(RegistrationColumn, normalized, ct);
        }
    }

    internal async Task<VendorDto> MapAsync(Vendor v, CancellationToken ct) => new(
        v.Id, v.LegalName, v.LegalNameAr, v.TradeName, v.CountryCode,
        await protector.UnprotectAsync(TaxColumn, v.TaxIdCipher, ct), await protector.UnprotectAsync(RegistrationColumn, v.RegistrationNoCipher, ct),
        v.ContactEmail, v.ContactPhone, v.Status, v.VerificationStatus, v.StatusReason, v.CreatedAt, v.SubmittedAt, v.ApprovedAt);

    private static string? Trim(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
