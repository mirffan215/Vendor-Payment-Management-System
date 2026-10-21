using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Vpms.BuildingBlocks.Audit;
using Vpms.BuildingBlocks.Errors;
using Vpms.BuildingBlocks.Security;
using Vpms.BuildingBlocks.Security.Encryption;
using Vpms.BuildingBlocks.Tenancy;
using Vpms.Vendors.Application;
using Vpms.Vendors.Contracts;
using Vpms.Vendors.Domain;

namespace Vpms.Vendors.Infrastructure;

public sealed class BankDetailsService(
    VendorsDbContext db, IFieldProtector protector, IAuditPort audit, ICurrentUser current, TenantContext tenant, TimeProvider clock)
    : IBankDetailsService, IBankDetailsLookup, IBankChangeExpiryScan
{
    public const string IbanColumn = "vendor.BankDetails.Iban";
    public const string AccountColumn = "vendor.BankDetails.AccountNumber";
    public static readonly TimeSpan ChangeLifetime = TimeSpan.FromDays(14);

    private static readonly Regex Swift = new("^[A-Z]{6}[A-Z0-9]{2}([A-Z0-9]{3})?$", RegexOptions.Compiled);
    private static readonly Regex Country = new("^[A-Z]{2}$", RegexOptions.Compiled);
    private static readonly Regex Currency = new("^[A-Z]{3}$", RegexOptions.Compiled);

    public async Task<IReadOnlyList<BankDetailsDto>> ListAsync(Guid vendorId, CancellationToken ct = default) =>
        (await db.BankDetails.AsNoTracking().Where(b => b.VendorId == vendorId).OrderByDescending(b => b.CreatedAt).ToListAsync(ct)).Select(Map).ToList();

    public async Task<IReadOnlyList<BankDetailChangeDto>> ListChangesAsync(Guid? vendorId, string? status, CancellationToken ct = default)
    {
        var q = db.BankDetailChanges.AsNoTracking().AsQueryable();
        if (vendorId is { } v) q = q.Where(c => c.VendorId == v);
        if (!string.IsNullOrWhiteSpace(status)) q = q.Where(c => c.Status == status);
        var changes = await q.OrderByDescending(c => c.MadeAt).Take(500).ToListAsync(ct);
        var result = new List<BankDetailChangeDto>();
        foreach (var c in changes) result.Add(await MapAsync(c, ct));
        return result;
    }

    public async Task<BankDetailChangeDto> ProposeAsync(Guid vendorId, ProposeBankDetails r, CancellationToken ct = default) =>
        await ProposeCoreAsync(vendorId, r, ChangeSource.Staff, current.UserId, ct);

    /// <summary>Shared by the staff API and the vendor portal; the source decides which extra checks apply at approval.</summary>
    public async Task<BankDetailChangeDto> ProposeCoreAsync(Guid vendorId, ProposeBankDetails r, string source, Guid madeBy, CancellationToken ct)
    {
        var vendor = await db.Vendors.AsNoTracking().FirstOrDefaultAsync(v => v.Id == vendorId, ct) ?? throw new NotFoundException("Vendor");
        if (vendor.Status == VendorStatus.Rejected)
        {
            throw new ConflictException("invalid_transition", "Reopen the vendor before adding bank details.");
        }

        var (iban, account) = Validate(r);
        var currency = r.CurrencyCode;

        if (await db.BankDetailChanges.AnyAsync(c => c.VendorId == vendorId && c.Status == ChangeStatus.Pending
                && db.BankDetails.Any(b => b.Id == c.ProposedBankDetailsId && b.CurrencyCode == currency), ct))
        {
            throw new ConflictException("change_pending", "There is already a pending bank-detail change for this vendor and currency.");
        }

        var ibanIndex = iban is null ? null : await protector.BlindIndexAsync(IbanColumn, iban, ct);
        var accountIndex = account is null ? null : await protector.BlindIndexAsync(AccountColumn, account, ct);

        var current_ = await db.BankDetails.AsNoTracking().FirstOrDefaultAsync(b => b.VendorId == vendorId && b.CurrencyCode == currency && b.Status == BankDetailsStatus.Verified, ct);
        if (current_ is not null && ((ibanIndex is not null && current_.IbanIndex == ibanIndex) || (accountIndex is not null && current_.AccountNumberIndex == accountIndex)))
        {
            throw new ConflictException("no_change", "These are the bank details already in effect.");
        }

        var shared = await db.BankDetails.AsNoTracking().AnyAsync(b => b.VendorId != vendorId
            && (b.Status == BankDetailsStatus.Verified || b.Status == BankDetailsStatus.Pending)
            && ((ibanIndex != null && b.IbanIndex == ibanIndex) || (accountIndex != null && b.AccountNumberIndex == accountIndex && b.CountryCode == r.CountryCode)), ct);
        if (shared && !r.ConfirmSharedAccount)
        {
            throw new ConflictException("bank_account_shared", "Another vendor already uses this account. This can indicate fraud; confirm only if it is expected.");
        }

        var now = clock.GetUtcNow();
        var details = new BankDetails
        {
            Id = Guid.NewGuid(), VendorId = vendorId, AccountName = r.AccountName.Trim(), BankName = r.BankName.Trim(),
            SwiftBic = string.IsNullOrWhiteSpace(r.SwiftBic) ? null : r.SwiftBic.Trim().ToUpperInvariant(), CountryCode = r.CountryCode, CurrencyCode = currency,
            IbanCipher = iban is null ? null : await protector.ProtectAsync(IbanColumn, iban, ct), IbanIndex = ibanIndex,
            AccountNumberCipher = account is null ? null : await protector.ProtectAsync(AccountColumn, account, ct), AccountNumberIndex = accountIndex,
            MaskedAccount = AuditRedactor.MaskTail(iban ?? account), Status = BankDetailsStatus.Pending, CreatedAt = now,
        };
        var change = new BankDetailChange
        {
            Id = Guid.NewGuid(), VendorId = vendorId, ProposedBankDetailsId = details.Id, ReplacesBankDetailsId = current_?.Id,
            Reason = r.Reason.Trim(), Source = source, MadeBy = madeBy, MadeAt = now, ExpiresAt = now + ChangeLifetime, Status = ChangeStatus.Pending,
        };
        db.BankDetails.Add(details);
        db.BankDetailChanges.Add(change);
        audit.Record(new AuditEntry("bank-details.change.proposed", "BankDetailChange", change.Id.ToString(), ActorId: madeBy,
            ChangeSummaryJson: JsonSerializer.Serialize(new
            {
                vendorId, source, currency, account = details.MaskedAccount, replaces = current_?.Id, sharedAccountConfirmed = shared && r.ConfirmSharedAccount,
            })));
        await db.SaveChangesAsync(ct);
        return await MapAsync(change, ct);
    }

    public async Task<BankDetailChangeDto> ApproveAsync(Guid changeId, string? callbackVerificationRef, string? comment, CancellationToken ct = default)
    {
        var change = await LoadChange(changeId, ct);
        var proposed = await db.BankDetails.FirstAsync(b => b.Id == change.ProposedBankDetailsId, ct);
        BankDetails? replaced = null;
        if (change.ReplacesBankDetailsId is { } oldId)
        {
            replaced = await db.BankDetails.FirstOrDefaultAsync(b => b.Id == oldId, ct);
            if (replaced is null || replaced.Status != BankDetailsStatus.Verified)
            {
                throw new ConflictException("stale_change", "The bank details this change replaces are no longer in effect. Start a new request.");
            }
        }

        var now = clock.GetUtcNow();
        try
        {
            change.Approve(current.UserId, callbackVerificationRef, comment, now);
        }
        catch (SegregationOfDutiesException ex)
        {
            await audit.WriteNowAsync(new AuditEntry("sod.bank-details.blocked", "BankDetailChange", changeId.ToString(),
                ChangeSummaryJson: JsonSerializer.Serialize(new { attempted = "approve", rule = ex.Rule, vendorId = change.VendorId })), ct);
            throw;
        }

        // Three steps in one transaction, in this order: the decision, retiring the old details, activating the new ones.
        // The database refuses to verify details whose change has not been approved by a different person.
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        audit.Record(new AuditEntry("bank-details.change.approved", "BankDetailChange", changeId.ToString(),
            ChangeSummaryJson: JsonSerializer.Serialize(new { vendorId = change.VendorId, callbackRecorded = change.CallbackVerificationRef is not null })));
        await db.SaveChangesAsync(ct);

        if (replaced is not null)
        {
            replaced.Status = BankDetailsStatus.Superseded;
            await db.SaveChangesAsync(ct);
        }

        proposed.Status = BankDetailsStatus.Verified;
        proposed.VerifiedAt = now;
        audit.Record(new AuditEntry("bank-details.activated", "BankDetails", proposed.Id.ToString(),
            ChangeSummaryJson: JsonSerializer.Serialize(new { vendorId = change.VendorId, currency = proposed.CurrencyCode, account = proposed.MaskedAccount, superseded = replaced?.Id })));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await MapAsync(change, ct);
    }

    public async Task<BankDetailChangeDto> RejectAsync(Guid changeId, string comment, CancellationToken ct = default)
    {
        var change = await LoadChange(changeId, ct);
        try
        {
            change.Reject(current.UserId, comment, clock.GetUtcNow());
        }
        catch (SegregationOfDutiesException ex)
        {
            await audit.WriteNowAsync(new AuditEntry("sod.bank-details.blocked", "BankDetailChange", changeId.ToString(),
                ChangeSummaryJson: JsonSerializer.Serialize(new { attempted = "reject", rule = ex.Rule })), ct);
            throw;
        }

        await Close(change, "bank-details.change.rejected", ct);
        return await MapAsync(change, ct);
    }

    public async Task<BankDetailChangeDto> CancelAsync(Guid changeId, CancellationToken ct = default)
    {
        var change = await LoadChange(changeId, ct);
        change.Cancel(current.UserId, clock.GetUtcNow());
        await Close(change, "bank-details.change.cancelled", ct);
        return await MapAsync(change, ct);
    }

    public async Task<RevealedBankDetails> RevealAsync(Guid bankDetailsId, CancellationToken ct = default)
    {
        var b = await db.BankDetails.AsNoTracking().FirstOrDefaultAsync(x => x.Id == bankDetailsId, ct) ?? throw new NotFoundException("Bank details");
        audit.Record(new AuditEntry("bank-details.revealed", "BankDetails", bankDetailsId.ToString(),
            ChangeSummaryJson: JsonSerializer.Serialize(new { vendorId = b.VendorId, account = b.MaskedAccount })));
        await db.SaveChangesAsync(ct); // writes the audit record before anything is returned
        return new RevealedBankDetails(b.Id, await protector.UnprotectAsync(IbanColumn, b.IbanCipher, ct), await protector.UnprotectAsync(AccountColumn, b.AccountNumberCipher, ct));
    }

    public async Task<int> ExpireStaleAsync(CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var stale = await db.BankDetailChanges.Where(c => c.Status == ChangeStatus.Pending && c.ExpiresAt <= now).ToListAsync(ct);
        foreach (var change in stale)
        {
            change.Expire();
            var proposed = await db.BankDetails.FirstAsync(b => b.Id == change.ProposedBankDetailsId, ct);
            audit.Record(new AuditEntry("bank-details.change.expired", "BankDetailChange", change.Id.ToString(), ActorType: AuditActorType.System,
                ChangeSummaryJson: JsonSerializer.Serialize(new { vendorId = change.VendorId })));
            await db.SaveChangesAsync(ct);
            proposed.Status = BankDetailsStatus.Revoked;
            await db.SaveChangesAsync(ct);
        }

        return stale.Count;
    }

    public async Task<IReadOnlyList<Guid>> TenantsWithStaleChangesAsync(CancellationToken ct = default)
    {
        using var _ = tenant.BeginPlatformScope();
        var now = clock.GetUtcNow();
        return await db.BankDetailChanges.AsNoTracking().Where(c => c.Status == ChangeStatus.Pending && c.ExpiresAt <= now)
            .Select(c => c.TenantId).Distinct().ToListAsync(ct);
    }

    public async Task<VerifiedBankDetails?> GetVerifiedAsync(Guid vendorId, string currencyCode, CancellationToken ct = default)
    {
        var b = await db.BankDetails.AsNoTracking().FirstOrDefaultAsync(x => x.VendorId == vendorId && x.CurrencyCode == currencyCode && x.Status == BankDetailsStatus.Verified, ct);
        return b is null ? null : new VerifiedBankDetails(b.Id, b.MaskedAccount, b.CurrencyCode, b.VerifiedAt!.Value, b.IbanIndex ?? b.AccountNumberIndex ?? b.Id.ToString("N"));
    }

    public Task<bool> HasPendingChangeAsync(Guid vendorId, CancellationToken ct = default) =>
        db.BankDetailChanges.AnyAsync(c => c.VendorId == vendorId && c.Status == ChangeStatus.Pending, ct);

    // --- internals -----------------------------------------------------------------------------------------

    private async Task Close(BankDetailChange change, string action, CancellationToken ct)
    {
        var proposed = await db.BankDetails.FirstAsync(b => b.Id == change.ProposedBankDetailsId, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        audit.Record(new AuditEntry(action, "BankDetailChange", change.Id.ToString(), ChangeSummaryJson: JsonSerializer.Serialize(new { vendorId = change.VendorId })));
        await db.SaveChangesAsync(ct);
        proposed.Status = BankDetailsStatus.Revoked;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    private async Task<BankDetailChange> LoadChange(Guid id, CancellationToken ct) =>
        await db.BankDetailChanges.FirstOrDefaultAsync(c => c.Id == id, ct) ?? throw new NotFoundException("Bank-detail change");

    private static (string? Iban, string? Account) Validate(ProposeBankDetails r)
    {
        if (string.IsNullOrWhiteSpace(r.AccountName) || r.AccountName.Length > 200) throw new ValidationFailedException("invalid_account_name", "The account holder name is required.");
        if (string.IsNullOrWhiteSpace(r.BankName) || r.BankName.Length > 200) throw new ValidationFailedException("invalid_bank_name", "The bank name is required.");
        if (!Country.IsMatch(r.CountryCode ?? "")) throw new ValidationFailedException("invalid_country", "The bank country must be an ISO 3166 alpha-2 code.");
        if (!Currency.IsMatch(r.CurrencyCode ?? "")) throw new ValidationFailedException("invalid_currency", "The currency must be an ISO 4217 code.");
        if (string.IsNullOrWhiteSpace(r.Reason) || r.Reason.Length > 500) throw new ValidationFailedException("reason_required", "A reason for the change is required.");
        if (!string.IsNullOrWhiteSpace(r.SwiftBic) && !Swift.IsMatch(r.SwiftBic.Trim().ToUpperInvariant())) throw new ValidationFailedException("invalid_swift", "The SWIFT/BIC code looks invalid.");

        string? iban = null;
        if (!string.IsNullOrWhiteSpace(r.Iban))
        {
            if (!Iban.IsValid(r.Iban)) throw new ValidationFailedException("invalid_iban", "The IBAN is not valid. Check the number.");
            iban = Iban.Normalize(r.Iban);
            if (iban[..2] != r.CountryCode) throw new ValidationFailedException("iban_country_mismatch", "The IBAN country does not match the bank country.");
        }
        else if (Iban.IsRequiredFor(r.CountryCode!))
        {
            throw new ValidationFailedException("iban_required", "An IBAN is required for banks in this country.");
        }

        string? account = null;
        if (!string.IsNullOrWhiteSpace(r.AccountNumber))
        {
            account = VendorValidation.NormalizeId(r.AccountNumber);
            if (account.Length is < 4 or > 34) throw new ValidationFailedException("invalid_account_number", "The account number looks invalid.");
        }

        if (iban is null && account is null) throw new ValidationFailedException("account_required", "Provide an IBAN or an account number.");
        return (iban, account);
    }

    private static BankDetailsDto Map(BankDetails b) => new(b.Id, b.VendorId, b.AccountName, b.BankName, b.SwiftBic, b.CountryCode, b.CurrencyCode,
        b.MaskedAccount, b.Status, b.CreatedAt, b.VerifiedAt);

    private async Task<BankDetailChangeDto> MapAsync(BankDetailChange c, CancellationToken ct)
    {
        var proposed = await db.BankDetails.AsNoTracking().FirstAsync(b => b.Id == c.ProposedBankDetailsId, ct);
        var current_ = c.ReplacesBankDetailsId is { } id ? await db.BankDetails.AsNoTracking().FirstOrDefaultAsync(b => b.Id == id, ct) : null;
        var vendorName = await db.Vendors.AsNoTracking().Where(v => v.Id == c.VendorId).Select(v => v.LegalName).FirstAsync(ct);
        return new BankDetailChangeDto(c.Id, c.VendorId, vendorName, c.ProposedBankDetailsId, c.ReplacesBankDetailsId, c.Reason, c.Source, c.MadeBy, c.MadeAt,
            c.ExpiresAt, c.Status, c.CheckedBy, c.CheckedAt, c.CheckerComment, c.CallbackVerificationRef, c.RequiresCallback, Map(proposed), current_ is null ? null : Map(current_));
    }
}
