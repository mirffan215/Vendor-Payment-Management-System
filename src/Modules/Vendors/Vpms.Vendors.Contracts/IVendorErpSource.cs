namespace Vpms.Vendors.Contracts;

/// <param name="SyncHash">Changes whenever a field the ERP holds changes, so a changed vendor is queued for sync again.</param>
public sealed record VendorErpData(
    Guid VendorId, Guid LegalEntityId, string LegalName, string CountryCode, string? TaxNumber, string CurrencyCode, string? PaymentTermsCode, string? ErpAccount, string SyncHash);

/// <summary>Vendor data for the ERP module, and the one write it may make: recording the account number the ERP assigned.</summary>
public interface IVendorErpSource
{
    /// <summary>The vendor as linked to the entity, or null when it is not Active and linked. The tax number is decrypted for the ERP call only.</summary>
    Task<VendorErpData?> GetAsync(Guid vendorId, Guid legalEntityId, CancellationToken ct = default);

    /// <summary>Active vendors linked to the entity, for syncing to the ERP.</summary>
    Task<IReadOnlyList<VendorErpData>> ListActiveAsync(Guid legalEntityId, int take, CancellationToken ct = default);

    /// <summary>Stores the ERP's account number on the link (audited). A different account already on the link is a conflict, never silently replaced.</summary>
    Task LinkErpAccountAsync(Guid vendorId, Guid legalEntityId, string erpAccount, CancellationToken ct = default);
}
