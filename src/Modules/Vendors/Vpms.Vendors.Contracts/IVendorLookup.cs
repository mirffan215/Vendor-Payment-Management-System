namespace Vpms.Vendors.Contracts;

public sealed record VendorSummary(Guid Id, string LegalName, string Status, string CountryCode);

/// <summary>For modules (invoices, payments) that must know whether a vendor may be transacted with.</summary>
public interface IVendorLookup
{
    Task<VendorSummary?> FindAsync(Guid vendorId, CancellationToken ct = default);

    /// <summary>True when the vendor is Active and linked (and not blocked) for the legal entity.</summary>
    Task<bool> CanTransactAsync(Guid vendorId, Guid legalEntityId, CancellationToken ct = default);
}
