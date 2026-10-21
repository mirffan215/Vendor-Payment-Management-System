namespace Vpms.Vendors.Contracts;

/// <param name="Fingerprint">Opaque value that changes whenever the account behind the vendor's verified details changes.</param>
public sealed record VerifiedBankDetails(Guid Id, string MaskedAccount, string CurrencyCode, DateTimeOffset VerifiedAt, string Fingerprint);

/// <summary>For payment preparation (Phase 5): which bank details are verified, and is a change in flight?</summary>
public interface IBankDetailsLookup
{
    Task<VerifiedBankDetails?> GetVerifiedAsync(Guid vendorId, string currencyCode, CancellationToken ct = default);

    /// <summary>True while any bank-detail change for the vendor awaits a decision. Payments should not be released meanwhile.</summary>
    Task<bool> HasPendingChangeAsync(Guid vendorId, CancellationToken ct = default);
}
