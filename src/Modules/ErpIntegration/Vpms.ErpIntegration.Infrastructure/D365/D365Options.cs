namespace Vpms.ErpIntegration.Infrastructure.D365;

public sealed class D365Options
{
    public const string Section = "Erp:D365";

    /// <summary>The Entra authority that issues tokens. Sovereign clouds use a different host.</summary>
    public string Authority { get; set; } = "https://login.microsoftonline.com";

    /// <summary>Lets the ERP address point at a private or loopback host. Development and tests only.</summary>
    public bool AllowInternalHosts { get; set; }
}

/// <summary>
/// The OData entity-set names used when a connection does not name its own. They are the documented Dynamics 365 Finance data entities,
/// but names and fields differ between versions and customisations, which is why each can be overridden per connection.
/// </summary>
public static class D365Defaults
{
    public static readonly IReadOnlyDictionary<string, string> EntitySets = new Dictionary<string, string>
    {
        ["vendors"] = "VendorsV2",
        ["invoiceJournalHeaders"] = "VendorInvoiceJournalHeaders",
        ["invoiceJournalLines"] = "VendorInvoiceJournalLines",
        ["invoiceAttachments"] = "DocumentAttachments",
        ["vendorInvoices"] = "VendorInvoiceHeaders",
        ["vendorPayments"] = "VendorPaymentJournalLines",
        ["purchaseOrders"] = "PurchaseOrderHeadersV2",
        ["purchaseOrderLines"] = "PurchaseOrderLinesV2",
        ["dimensionValues"] = "FinancialDimensionValues",
    };
}
