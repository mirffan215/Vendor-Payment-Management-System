namespace Vpms.Vendors.Contracts;

/// <param name="Fingerprint">Opaque value that changes whenever the account behind the vendor's verified details changes.</param>
/// <param name="MadeBy">Who proposed the change that made these details Verified (null for details that predate change records).</param>
/// <param name="CheckedBy">Who approved that change.</param>
public sealed record VerifiedBankDetails(Guid Id, string MaskedAccount, string CurrencyCode, DateTimeOffset VerifiedAt, string Fingerprint, Guid? MadeBy = null, Guid? CheckedBy = null);

/// <summary>Everything a payment instruction needs to name the beneficiary account. Only returned by <see cref="IBankDetailsLookup.RevealForPaymentAsync"/>.</summary>
public sealed record PaymentBankDetails(Guid Id, string AccountName, string BankName, string? SwiftBic, string CountryCode, string CurrencyCode, string? Iban, string? AccountNumber);

/// <summary>For payment preparation (Phase 5): which bank details are verified, and is a change in flight?</summary>
public interface IBankDetailsLookup
{
    Task<VerifiedBankDetails?> GetVerifiedAsync(Guid vendorId, string currencyCode, CancellationToken ct = default);

    /// <summary>True while any bank-detail change for the vendor awaits a decision. Payments should not be released meanwhile.</summary>
    Task<bool> HasPendingChangeAsync(Guid vendorId, CancellationToken ct = default);

    /// <summary>
    /// The full beneficiary account for a payment instruction. Writes an audit record (before returning) and refuses details that
    /// are not currently Verified. The caller must never log or expose the result outside the instruction.
    /// </summary>
    Task<PaymentBankDetails> RevealForPaymentAsync(Guid bankDetailsId, CancellationToken ct = default);
}
