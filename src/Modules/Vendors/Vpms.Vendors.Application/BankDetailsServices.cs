namespace Vpms.Vendors.Application;

public sealed record BankDetailsDto(
    Guid Id, Guid VendorId, string AccountName, string BankName, string? SwiftBic, string CountryCode, string CurrencyCode,
    string MaskedAccount, string Status, DateTimeOffset CreatedAt, DateTimeOffset? VerifiedAt);

public sealed record BankDetailChangeDto(
    Guid Id, Guid VendorId, string VendorName, Guid ProposedBankDetailsId, Guid? ReplacesBankDetailsId, string Reason, string Source,
    Guid MadeBy, DateTimeOffset MadeAt, DateTimeOffset ExpiresAt, string Status, Guid? CheckedBy, DateTimeOffset? CheckedAt,
    string? CheckerComment, string? CallbackVerificationRef, bool RequiresCallback, BankDetailsDto Proposed, BankDetailsDto? Current);

public sealed record ProposeBankDetails(
    string AccountName, string BankName, string? SwiftBic, string CountryCode, string CurrencyCode, string? Iban, string? AccountNumber,
    string Reason, bool ConfirmSharedAccount = false);

public sealed record RevealedBankDetails(Guid Id, string? Iban, string? AccountNumber);

public interface IBankDetailsService
{
    Task<IReadOnlyList<BankDetailsDto>> ListAsync(Guid vendorId, CancellationToken ct = default);

    Task<IReadOnlyList<BankDetailChangeDto>> ListChangesAsync(Guid? vendorId, string? status, CancellationToken ct = default);

    /// <summary>Proposes new bank details. Nothing takes effect until a different person approves.</summary>
    Task<BankDetailChangeDto> ProposeAsync(Guid vendorId, ProposeBankDetails request, CancellationToken ct = default);

    Task<BankDetailChangeDto> ApproveAsync(Guid changeId, string? callbackVerificationRef, string? comment, CancellationToken ct = default);

    Task<BankDetailChangeDto> RejectAsync(Guid changeId, string comment, CancellationToken ct = default);

    Task<BankDetailChangeDto> CancelAsync(Guid changeId, CancellationToken ct = default);

    /// <summary>Shows the full account identifiers. Requires a dedicated permission and is always audited.</summary>
    Task<RevealedBankDetails> RevealAsync(Guid bankDetailsId, CancellationToken ct = default);

    /// <summary>Marks changes that waited too long as expired. Run by the worker for every tenant.</summary>
    Task<int> ExpireStaleAsync(CancellationToken ct = default);
}

/// <summary>Cross-tenant listing of tenants that have stale pending bank-detail changes (platform scope).</summary>
public interface IBankChangeExpiryScan
{
    Task<IReadOnlyList<Guid>> TenantsWithStaleChangesAsync(CancellationToken ct = default);
}
