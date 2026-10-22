using Vpms.Invoices.Domain.Verification;

namespace Vpms.Invoices.Domain.Tax;

/// <param name="Settings">The tax.vat rule's parameters for the entity (switches are 0 or 1), so a provider honours what the entity turned on.</param>
public sealed record TaxInput(InvoiceFacts Invoice, string? VendorTaxNumber, IReadOnlyDictionary<string, decimal> Settings)
{
    public bool On(string setting) => Settings.TryGetValue(setting, out var v) && v >= 1m;
}

/// <summary>
/// Country-specific checks on a RECEIVED supplier invoice. Providers only validate what a supplier sent; none of them issues, signs or
/// reports an invoice. They plug in from separate assemblies and are chosen by the legal entity's country.
/// </summary>
public interface ITaxRuleProvider
{
    /// <summary>ISO 3166 alpha-2 code the provider serves, or "*" for the fallback used when no country-specific provider exists.</summary>
    string CountryCode { get; }

    string Name { get; }

    IReadOnlyList<Finding> Evaluate(TaxInput input);
}

/// <summary>Names of the tax.vat switches. A provider honours the ones that make sense for its country and ignores the rest.</summary>
public static class TaxSettings
{
    public const string CheckSupplierTaxNumberFormat = "checkSupplierTaxNumberFormat";
    public const string RequireSupplierTaxNumber = "requireSupplierTaxNumber";
    public const string MatchVendorTaxNumber = "matchVendorTaxNumber";
    public const string CheckSupplierTaxInvoiceRefFormat = "checkSupplierTaxInvoiceRefFormat";
    public const string RequireSupplierTaxInvoiceRef = "requireSupplierTaxInvoiceRef";
    public const string CheckAllowedRates = "checkAllowedRates";
}

/// <summary>The fallback for countries without a provider: no country-specific rules; the rule's VAT arithmetic still runs.</summary>
public sealed class StandardVatProvider : ITaxRuleProvider
{
    public string CountryCode => "*";

    public string Name => "Standard VAT";

    public IReadOnlyList<Finding> Evaluate(TaxInput input)
    {
        var findings = new List<Finding>();
        var number = input.Invoice.SupplierTaxNumber?.Trim();
        if (input.On(TaxSettings.RequireSupplierTaxNumber) && string.IsNullOrEmpty(number)) findings.Add(Finding.Of("tax_number_required"));
        if (input.On(TaxSettings.MatchVendorTaxNumber) && !string.IsNullOrEmpty(number) && !string.IsNullOrEmpty(input.VendorTaxNumber)
            && Normalize(number) != Normalize(input.VendorTaxNumber))
        {
            findings.Add(Finding.Of("tax_number_vendor_mismatch"));
        }

        if (input.On(TaxSettings.RequireSupplierTaxInvoiceRef) && string.IsNullOrWhiteSpace(input.Invoice.SupplierTaxInvoiceRef)) findings.Add(Finding.Of("tax_ref_required"));
        return findings;
    }

    public static string Normalize(string value) => new(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
}
