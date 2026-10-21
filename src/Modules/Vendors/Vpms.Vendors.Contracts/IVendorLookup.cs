namespace Vpms.Vendors.Contracts;

public sealed record VendorSummary(Guid Id, string LegalName, string Status, string CountryCode);

/// <summary>For modules (invoices, payments) that must know whether a vendor may be transacted with.</summary>
public interface IVendorLookup
{
    Task<VendorSummary?> FindAsync(Guid vendorId, CancellationToken ct = default);

    /// <summary>Active vendors linked to the entity whose contact email matches (case-insensitive). More than one match means ambiguous.</summary>
    Task<IReadOnlyList<Guid>> FindActiveByContactEmailAsync(string email, Guid legalEntityId, CancellationToken ct = default);

    /// <summary>The currency the entity defaults to for this vendor (from the vendor-entity link), or null when not linked.</summary>
    Task<string?> GetLinkCurrencyAsync(Guid vendorId, Guid legalEntityId, CancellationToken ct = default);

    /// <summary>The vendor the entity knows under this ERP vendor account, or null.</summary>
    Task<Guid?> FindByErpAccountAsync(string erpVendorAccount, Guid legalEntityId, CancellationToken ct = default);

    /// <summary>True when the vendor is Active and linked (and not blocked) for the legal entity.</summary>
    Task<bool> CanTransactAsync(Guid vendorId, Guid legalEntityId, CancellationToken ct = default);
}
