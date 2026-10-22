using Vpms.Invoices.Domain.Tax;
using Vpms.Invoices.Domain.Verification;
using Vpms.Invoices.Domain.Verification.Rules;
using Vpms.Tax.Sa;

namespace Vpms.UnitTests.Invoices;

public class SaudiVatProviderTests
{
    private static readonly SaudiVatProvider Provider = new();

    private static IReadOnlyDictionary<string, decimal> Settings(Action<Dictionary<string, decimal>>? tweak = null)
    {
        var d = new Dictionary<string, decimal>(RuleConfig.Defaults().Get(RuleCodes.TaxVat).Parameters);
        tweak?.Invoke(d);
        return d;
    }

    private static InvoiceFacts Invoice(string? taxNumber = "300012345600003", string? reference = null, DateOnly? date = null, params LineFacts[] lines) =>
        Rf.Invoice(date: date ?? Rf.Today.AddDays(-5), lines: lines.Length == 0 ? [Rf.Line()] : lines) with { SupplierTaxNumber = taxNumber, SupplierTaxInvoiceRef = reference };

    private static IReadOnlyList<Finding> Run(InvoiceFacts invoice, string? vendorNumber = null, Action<Dictionary<string, decimal>>? tweak = null) =>
        Provider.Evaluate(new TaxInput(invoice, vendorNumber, Settings(tweak)));

    private static LineFacts SaLine(string? code, decimal rate, decimal tax, decimal amount = 100m) => new(1, "Item", 1, amount, amount, code, rate, tax, null);

    [Theory]
    [InlineData("300012345600003", true)]
    [InlineData("310122393500003", true)]
    [InlineData("399999999900003", true)]
    [InlineData("200012345600003", false)]   // must start with 3
    [InlineData("300012345600004", false)]   // must end with 3
    [InlineData("30001234560003", false)]    // 14 digits
    [InlineData("3000123456000033", false)]  // 16 digits
    [InlineData("3000123456A0003", false)]   // not numeric
    [InlineData("", false)]
    [InlineData(null, false)]
    public void The_vat_registration_number_has_15_digits_and_starts_and_ends_with_3(string? number, bool valid) =>
        Assert.Equal(valid, SaudiVatProvider.IsValidVatNumber(number));

    [Fact]
    public void A_correct_invoice_passes() => Assert.Empty(Run(Invoice(reference: Guid.NewGuid().ToString()), "300012345600003"));

    [Fact]
    public void A_malformed_supplier_number_is_reported_and_masked()
    {
        var f = Assert.Single(Run(Invoice("123")));
        Assert.Equal("tax_number_invalid_format", f.Key);
        Assert.DoesNotContain("123456", f.Evidence["value"]);
    }

    [Fact]
    public void Spaces_and_dashes_in_the_printed_number_are_ignored() => Assert.Empty(Run(Invoice("3000 1234-5600 003")));

    [Fact]
    public void A_missing_number_is_only_a_finding_when_it_is_required()
    {
        Assert.Empty(Run(Invoice(null)));
        Assert.Equal("tax_number_required", Assert.Single(Run(Invoice(null), tweak: s => s[TaxSettings.RequireSupplierTaxNumber] = 1m)).Key);
        Assert.Equal("tax_number_required", Assert.Single(Run(Invoice("  "), tweak: s => s[TaxSettings.RequireSupplierTaxNumber] = 1m)).Key);
    }

    [Fact]
    public void The_number_on_the_invoice_must_match_the_vendor_master()
    {
        var f = Assert.Single(Run(Invoice("300012345600003"), vendorNumber: "310122393500003"));
        Assert.Equal("tax_number_vendor_mismatch", f.Key);
        Assert.Empty(Run(Invoice("300012345600003"), vendorNumber: "300012345600003"));
        Assert.Empty(Run(Invoice("300012345600003"), vendorNumber: null));   // nothing on file to compare with
        Assert.Empty(Run(Invoice("300012345600003"), vendorNumber: "310122393500003", tweak: s => s[TaxSettings.MatchVendorTaxNumber] = 0m));
    }

    [Fact]
    public void The_format_check_can_be_switched_off() => Assert.Empty(Run(Invoice("123"), tweak: s => s[TaxSettings.CheckSupplierTaxNumberFormat] = 0m));

    [Fact]
    public void The_supplier_invoice_reference_is_a_guid_when_given_and_required_only_when_switched_on()
    {
        Assert.Empty(Run(Invoice(reference: null)));
        Assert.Equal("tax_ref_invalid_format", Assert.Single(Run(Invoice(reference: "not-a-uuid"))).Key);
        Assert.Empty(Run(Invoice(reference: "not-a-uuid"), tweak: s => s[TaxSettings.CheckSupplierTaxInvoiceRefFormat] = 0m));
        Assert.Equal("tax_ref_required", Assert.Single(Run(Invoice(reference: null), tweak: s => s[TaxSettings.RequireSupplierTaxInvoiceRef] = 1m)).Key);
    }

    [Theory]
    [InlineData("S", 0.15, 15, 0)]
    [InlineData("s", 0.15, 15, 0)]
    [InlineData("Z", 0, 0, 0)]
    [InlineData("E", 0, 0, 0)]
    [InlineData("O", 0, 0, 0)]
    public void Valid_tax_categories_pass(string code, double rate, double tax, int expectedFindings) =>
        Assert.Equal(expectedFindings, Run(Invoice(lines: SaLine(code, (decimal)rate, (decimal)tax))).Count);

    [Fact]
    public void The_standard_rate_is_fifteen_percent_now_and_was_five_before_july_2020()
    {
        Assert.Equal("tax_rate_not_allowed", Assert.Single(Run(Invoice(lines: SaLine("S", 0.05m, 5m)))).Key);
        Assert.Equal("tax_rate_not_allowed", Assert.Single(Run(Invoice(lines: SaLine("S", 0.10m, 10m)))).Key);

        var before = new DateOnly(2020, 6, 30);
        Assert.Empty(Run(Invoice(date: before, lines: SaLine("S", 0.05m, 5m))));
        Assert.Equal("tax_rate_not_allowed", Assert.Single(Run(Invoice(date: before, lines: SaLine("S", 0.15m, 15m)))).Key);
        Assert.Empty(Run(Invoice(date: SaudiVatProvider.RateChangeDate, lines: SaLine("S", 0.15m, 15m))));
    }

    [Fact]
    public void Zero_rated_and_exempt_lines_cannot_carry_tax_and_need_a_zero_rate()
    {
        Assert.Equal("tax_amount_on_non_taxable", Assert.Single(Run(Invoice(lines: SaLine("Z", 0m, 3m)))).Key);
        Assert.Equal("tax_rate_not_allowed", Assert.Single(Run(Invoice(lines: SaLine("E", 0.15m, 15m)))).Key);
    }

    [Fact]
    public void Unknown_and_missing_tax_codes_are_reported_per_line()
    {
        var findings = Run(Invoice(lines: [SaLine("X", 0.15m, 15m) with { LineNo = 1 }, SaLine(null, 0.15m, 15m) with { LineNo = 2 }]));
        Assert.Equal(["tax_code_unknown", "tax_code_missing"], findings.Select(f => f.Key).ToArray());
        Assert.Empty(Run(Invoice(lines: SaLine("X", 0.15m, 15m)), tweak: s => s[TaxSettings.CheckAllowedRates] = 0m));
    }
}

public class StandardVatProviderTests
{
    private static readonly StandardVatProvider Provider = new();

    private static IReadOnlyList<Finding> Run(InvoiceFacts invoice, string? vendorNumber = null, Action<Dictionary<string, decimal>>? tweak = null)
    {
        var d = new Dictionary<string, decimal>(RuleConfig.Defaults().Get(RuleCodes.TaxVat).Parameters);
        tweak?.Invoke(d);
        return Provider.Evaluate(new TaxInput(invoice, vendorNumber, d));
    }

    [Fact]
    public void It_serves_every_country_as_the_fallback() => Assert.Equal("*", Provider.CountryCode);

    [Fact]
    public void Any_number_format_is_accepted_but_presence_and_vendor_match_can_be_checked()
    {
        var invoice = Rf.Invoice() with { SupplierTaxNumber = "GB123456789" };
        Assert.Empty(Run(invoice));
        Assert.Equal("tax_number_vendor_mismatch", Assert.Single(Run(invoice, "GB999999999")).Key);
        Assert.Empty(Run(invoice, "gb 123-456-789"));
        Assert.Equal("tax_number_required", Assert.Single(Run(Rf.Invoice(), tweak: s => s[TaxSettings.RequireSupplierTaxNumber] = 1m)).Key);
        Assert.Equal("tax_ref_required", Assert.Single(Run(Rf.Invoice(), tweak: s => s[TaxSettings.RequireSupplierTaxInvoiceRef] = 1m)).Key);
    }
}

public class TaxVatRuleWithProviderTests
{
    [Fact]
    public void Without_a_provider_only_the_arithmetic_runs()
    {
        var input = Rf.Input(Rf.Invoice() with { SupplierTaxNumber = "garbage" });
        Assert.Empty(new TaxVatRule().Evaluate(input, Rf.Setting(RuleCodes.TaxVat)));
    }

    [Fact]
    public void A_provider_adds_its_findings_to_the_arithmetic_ones()
    {
        var invoice = Rf.Invoice(lines: [Rf.Line(tax: 10m)]) with { SupplierTaxNumber = "garbage" };
        var input = Rf.Input(invoice) with { TaxProvider = new SaudiVatProvider() };
        var keys = new TaxVatRule().Evaluate(input, Rf.Setting(RuleCodes.TaxVat)).Select(f => f.Key).ToList();
        Assert.Contains("line_tax_mismatch", keys);
        Assert.Contains("tax_number_invalid_format", keys);
    }

    [Fact]
    public void The_entity_can_switch_individual_country_checks_off()
    {
        var invoice = Rf.Invoice() with { SupplierTaxNumber = "garbage" };
        var input = Rf.Input(invoice) with { TaxProvider = new SaudiVatProvider() };
        Assert.Empty(new TaxVatRule().Evaluate(input, Rf.Setting(RuleCodes.TaxVat, p => p[TaxSettings.CheckSupplierTaxNumberFormat] = 0m)));
    }

    [Fact]
    public void The_new_switches_are_in_the_catalogue_with_safe_defaults()
    {
        var tax = RuleConfig.Defaults().Get(RuleCodes.TaxVat);
        Assert.Equal(1m, tax.Parameters[TaxSettings.CheckSupplierTaxNumberFormat]);
        Assert.Equal(0m, tax.Parameters[TaxSettings.RequireSupplierTaxNumber]);
        Assert.Equal(1m, tax.Parameters[TaxSettings.CheckAllowedRates]);
        // turning a check off is a loosening and needs a second person
        var off = RuleConfig.Normalize(new Dictionary<string, RuleSetting>
        {
            [RuleCodes.TaxVat] = new(true, RuleSeverity.Flag, tax.Parameters.ToDictionary(p => p.Key, p => p.Key == TaxSettings.CheckAllowedRates ? 0m : p.Value)),
        });
        Assert.Equal([RuleCodes.TaxVat], RuleConfig.LoosenedRules(RuleConfig.Defaults(), off));
    }
}
