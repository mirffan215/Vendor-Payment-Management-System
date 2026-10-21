using System.Globalization;
using System.Text.RegularExpressions;
using Vpms.Invoices.Domain;
using Vpms.Invoices.Domain.Tax;
using Vpms.Invoices.Domain.Verification;

namespace Vpms.Tax.Sa;

/// <summary>
/// Checks on supplier invoices RECEIVED by a Saudi legal entity, so the customer can rely on them for input VAT and payment accuracy.
/// This module validates data a supplier sent. It does not issue, sign, clear or report invoices: VPMS is used for the payment process
/// only (see docs/zatca-scope.md). It is isolated on purpose: remove its registration and Saudi entities fall back to the standard checks.
/// </summary>
public sealed partial class SaudiVatProvider : ITaxRuleProvider
{
    /// <summary>The 15% standard rate applies from 1 July 2020; before that the rate was 5%.</summary>
    public static readonly DateOnly RateChangeDate = new(2020, 7, 1);

    public string CountryCode => "SA";

    public string Name => "Saudi VAT (receive side)";

    // Tax categories used on Saudi tax invoices: S standard, Z zero rated, E exempt, O outside the scope of VAT.
    private static readonly Dictionary<string, Func<DateOnly, decimal>> Categories = new(StringComparer.OrdinalIgnoreCase)
    {
        ["S"] = date => date < RateChangeDate ? 0.05m : 0.15m,
        ["Z"] = _ => 0m,
        ["E"] = _ => 0m,
        ["O"] = _ => 0m,
    };

    [GeneratedRegex(@"^3\d{13}3$")]
    private static partial Regex VatNumber();

    /// <summary>A Saudi VAT registration number is 15 digits and starts and ends with 3.</summary>
    public static bool IsValidVatNumber(string? value) => value is not null && VatNumber().IsMatch(value);

    public IReadOnlyList<Finding> Evaluate(TaxInput input)
    {
        var findings = new List<Finding>();
        var invoice = input.Invoice;
        var number = Compact(invoice.SupplierTaxNumber);

        if (input.On(TaxSettings.RequireSupplierTaxNumber) && number.Length == 0) findings.Add(Finding.Of("tax_number_required"));
        if (number.Length > 0)
        {
            if (input.On(TaxSettings.CheckSupplierTaxNumberFormat) && !IsValidVatNumber(number)) findings.Add(Finding.Of("tax_number_invalid_format", ("value", Mask(number))));
            var vendorNumber = Compact(input.VendorTaxNumber);
            if (input.On(TaxSettings.MatchVendorTaxNumber) && vendorNumber.Length > 0 && vendorNumber != number)
            {
                findings.Add(Finding.Of("tax_number_vendor_mismatch", ("invoice", Mask(number)), ("vendor", Mask(vendorNumber))));
            }
        }

        var reference = invoice.SupplierTaxInvoiceRef?.Trim();
        if (input.On(TaxSettings.RequireSupplierTaxInvoiceRef) && string.IsNullOrEmpty(reference)) findings.Add(Finding.Of("tax_ref_required"));
        if (!string.IsNullOrEmpty(reference) && input.On(TaxSettings.CheckSupplierTaxInvoiceRefFormat) && !Guid.TryParse(reference, out _))
        {
            findings.Add(Finding.Of("tax_ref_invalid_format")); // the e-invoice UUID printed on a ZATCA invoice is a GUID
        }

        if (input.On(TaxSettings.CheckAllowedRates))
        {
            foreach (var line in invoice.Lines)
            {
                if (string.IsNullOrWhiteSpace(line.TaxCode))
                {
                    findings.Add(Finding.Of("tax_code_missing", ("lineNo", line.LineNo.ToString(CultureInfo.InvariantCulture))));
                    continue;
                }

                if (!Categories.TryGetValue(line.TaxCode.Trim(), out var rateFor))
                {
                    findings.Add(Finding.Of("tax_code_unknown", ("lineNo", line.LineNo.ToString(CultureInfo.InvariantCulture)), ("taxCode", line.TaxCode)));
                    continue;
                }

                var expected = rateFor(invoice.InvoiceDate);
                if (line.TaxRate != expected)
                {
                    findings.Add(Finding.Of("tax_rate_not_allowed", ("lineNo", line.LineNo.ToString(CultureInfo.InvariantCulture)), ("taxCode", line.TaxCode),
                        ("rate", line.TaxRate.ToString("0.####", CultureInfo.InvariantCulture)), ("expected", expected.ToString("0.####", CultureInfo.InvariantCulture))));
                }
                else if (expected == 0m && line.TaxAmount != 0m)
                {
                    findings.Add(Finding.Of("tax_amount_on_non_taxable", ("lineNo", line.LineNo.ToString(CultureInfo.InvariantCulture)), ("taxCode", line.TaxCode)));
                }
            }
        }

        return findings;
    }

    /// <summary>The number without the spaces, dashes and dots people type between groups. Anything else stays, so a letter is still an error.</summary>
    private static string Compact(string? value) => value is null ? "" : new string(value.Where(c => !char.IsWhiteSpace(c) && c is not ( '-' or '.')).ToArray());

    private static string Mask(string digits) => digits.Length <= 4 ? digits : new string('•', digits.Length - 4) + digits[^4..];
}
