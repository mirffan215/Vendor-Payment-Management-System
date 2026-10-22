using Vpms.Invoices.Domain.Verification;
using Vpms.Invoices.Domain.Verification.Rules;

namespace Vpms.UnitTests.Invoices;

internal static class Rf
{
    public static readonly DateOnly Today = new(2026, 10, 21);
    public static readonly Guid Vendor = Guid.NewGuid();

    public static LineFacts Line(int no = 1, decimal qty = 1, decimal price = 100m, decimal? amount = null, decimal rate = 0.15m, decimal? tax = null, string? poLine = null) =>
        new(no, "Item", qty, price, amount ?? qty * price, "S", rate, tax ?? Math.Round((amount ?? qty * price) * rate, 2), poLine);

    public static InvoiceFacts Invoice(string number = "INV-100", decimal total = 115m, DateOnly? date = null, string currency = "SAR", string? po = null,
        decimal? subtotal = null, decimal? tax = null, IReadOnlyList<LineFacts>? lines = null, DateOnly? due = null) =>
        new(Guid.NewGuid(), Vendor, Guid.NewGuid(), number, date ?? new DateOnly(2026, 10, 1), due, currency,
            subtotal ?? Math.Round(total / 1.15m, 2), tax ?? total - Math.Round(total / 1.15m, 2), total, po, null, lines ?? [Line()]);

    public static CandidateFacts Candidate(string number = "INV-100", decimal total = 115m, DateOnly? date = null, string currency = "SAR", string status = "Received") =>
        new(Guid.NewGuid(), number, date ?? new DateOnly(2026, 10, 1), total, currency, status);

    public static VerificationInput Input(InvoiceFacts? invoice = null, IReadOnlyList<CandidateFacts>? candidates = null, PoFacts? po = null, bool canTransact = true) =>
        new(invoice ?? Invoice(), new VendorFacts(canTransact), candidates ?? [], po, Today);

    public static RuleSetting Setting(string code, Action<Dictionary<string, decimal>>? tweak = null)
    {
        var s = RuleConfig.Defaults().Get(code);
        var p = new Dictionary<string, decimal>(s.Parameters);
        tweak?.Invoke(p);
        return new RuleSetting(s.Enabled, s.Severity, p);
    }
}

public class DuplicateRuleTests
{
    private static IReadOnlyList<Finding> Exact(VerificationInput i) => new DuplicateExactRule().Evaluate(i, Rf.Setting(RuleCodes.DuplicateExact));
    private static IReadOnlyList<Finding> Same(VerificationInput i) => new DuplicateSameNumberRule().Evaluate(i, Rf.Setting(RuleCodes.DuplicateSameNumber));
    private static IReadOnlyList<Finding> Fuzzy(VerificationInput i, Action<Dictionary<string, decimal>>? tweak = null) =>
        new DuplicateFuzzyRule().Evaluate(i, Rf.Setting(RuleCodes.DuplicateFuzzy, tweak));

    [Fact]
    public void An_invoice_with_nothing_similar_passes()
    {
        var input = Rf.Input(candidates: [Rf.Candidate("ABC-9", 5000m, new DateOnly(2025, 1, 1))]);
        Assert.Empty(Exact(input));
        Assert.Empty(Same(input));
        Assert.Empty(Fuzzy(input));
    }

    [Theory]
    [InlineData("INV-100")]
    [InlineData("inv 100")]
    [InlineData("INV/0100")]
    [InlineData("INV-00100")]
    public void The_same_number_written_differently_with_the_same_amount_and_date_is_an_exact_duplicate(string other)
    {
        var finding = Assert.Single(Exact(Rf.Input(candidates: [Rf.Candidate(other)])));
        Assert.Equal("duplicate_exact", finding.Key);
    }

    [Fact]
    public void Exact_needs_the_same_amount_the_same_date_and_the_same_currency()
    {
        Assert.Empty(Exact(Rf.Input(candidates: [Rf.Candidate(total: 115.01m)])));
        Assert.Empty(Exact(Rf.Input(candidates: [Rf.Candidate(date: new DateOnly(2026, 10, 2))])));
        Assert.Empty(Exact(Rf.Input(candidates: [Rf.Candidate(currency: "AED")])));
    }

    [Fact]
    public void Amounts_that_round_to_the_same_value_are_the_same_amount() =>
        Assert.Single(Exact(Rf.Input(Rf.Invoice(total: 115.004m), candidates: [Rf.Candidate(total: 115.001m)])));

    [Fact]
    public void Several_matches_are_all_reported()
    {
        var input = Rf.Input(candidates: [Rf.Candidate(), Rf.Candidate("inv100")]);
        Assert.Equal(2, Exact(input).Count);
    }

    [Fact]
    public void The_same_number_with_another_amount_or_date_is_a_same_number_finding_not_an_exact_one()
    {
        var input = Rf.Input(candidates: [Rf.Candidate(total: 200m), Rf.Candidate(date: new DateOnly(2026, 9, 1))]);
        Assert.Empty(Exact(input));
        Assert.Equal(2, Same(input).Count);
        Assert.All(Same(input), f => Assert.Equal("duplicate_same_number", f.Key));
    }

    [Fact]
    public void An_exact_match_is_not_also_reported_as_same_number() => Assert.Empty(Same(Rf.Input(candidates: [Rf.Candidate()])));

    [Fact]
    public void An_invoice_without_a_number_is_never_a_duplicate()
    {
        var input = Rf.Input(Rf.Invoice(number: ""), [Rf.Candidate("")]);
        Assert.Empty(Exact(input));
        Assert.Empty(Same(input));
        Assert.Empty(Fuzzy(input));
    }

    [Fact]
    public void Fuzzy_catches_a_similar_number_with_nearly_the_same_amount_and_date()
    {
        // INV-10010 vs INV-10011: one character apart, same amount, two days apart
        var invoice = Rf.Invoice("INV-10010", 1150m, new DateOnly(2026, 10, 3));
        var f = Assert.Single(Fuzzy(Rf.Input(invoice, [Rf.Candidate("INV-10011", 1150m, new DateOnly(2026, 10, 1))])));
        Assert.Equal("duplicate_similar", f.Key);
        Assert.Equal("0.88", f.Evidence["similarity"].Replace(',', '.'));
    }

    [Fact]
    public void Fuzzy_respects_the_similarity_threshold()
    {
        var invoice = Rf.Invoice("INV-10010", 1150m);
        var input = Rf.Input(invoice, [Rf.Candidate("INV-10011", 1150m)]); // similarity 0.875
        Assert.Single(Fuzzy(input, p => p["numberSimilarity"] = 0.875m));   // boundary: equal passes the threshold
        Assert.Empty(Fuzzy(input, p => p["numberSimilarity"] = 0.876m));
    }

    [Fact]
    public void Fuzzy_respects_the_amount_tolerance_at_its_boundary()
    {
        var invoice = Rf.Invoice("INV-1001", 1000m);
        Assert.Single(Fuzzy(Rf.Input(invoice, [Rf.Candidate("INV-1010", 1010m)]), p => { p["amountTolerancePercent"] = 1m; p["numberSimilarity"] = 0.7m; }));   // exactly 1%
        Assert.Empty(Fuzzy(Rf.Input(invoice, [Rf.Candidate("INV-1010", 1010.01m)]), p => { p["amountTolerancePercent"] = 1m; p["numberSimilarity"] = 0.7m; }));
        Assert.Empty(Fuzzy(Rf.Input(invoice, [Rf.Candidate("INV-1010", 1000.5m)]), p => { p["amountTolerancePercent"] = 0m; p["numberSimilarity"] = 0.7m; }));
    }

    [Fact]
    public void Fuzzy_respects_the_date_window_at_its_boundary()
    {
        var invoice = Rf.Invoice("INV-1001", 1000m, new DateOnly(2026, 10, 10));
        Assert.Single(Fuzzy(Rf.Input(invoice, [Rf.Candidate("INV-1010", 1000m, new DateOnly(2026, 10, 3))]), p => { p["dateWindowDays"] = 7m; p["numberSimilarity"] = 0.7m; }));  // 7 days
        Assert.Empty(Fuzzy(Rf.Input(invoice, [Rf.Candidate("INV-1010", 1000m, new DateOnly(2026, 10, 2))]), p => { p["dateWindowDays"] = 7m; p["numberSimilarity"] = 0.7m; }));   // 8 days
    }

    [Fact]
    public void Fuzzy_ignores_other_currencies_and_unrelated_numbers()
    {
        var invoice = Rf.Invoice("INV-1001", 1000m);
        Assert.Empty(Fuzzy(Rf.Input(invoice, [Rf.Candidate("INV-1010", 1000m, currency: "AED")]), p => p["numberSimilarity"] = 0.7m));
        Assert.Empty(Fuzzy(Rf.Input(invoice, [Rf.Candidate("ZZZ-9", 1000m)])));
    }

    [Theory]
    [InlineData("kitten", "sitting", 3)]
    [InlineData("", "abc", 3)]
    [InlineData("abc", "abc", 0)]
    [InlineData("INV1001", "INV1010", 2)]
    public void Edit_distance_is_correct(string a, string b, int expected) => Assert.Equal(expected, DuplicateMath.Levenshtein(a, b));

    [Fact]
    public void Similarity_is_between_zero_and_one()
    {
        Assert.Equal(1m, DuplicateMath.Similarity("", ""));
        Assert.Equal(1m, DuplicateMath.Similarity("ABC", "ABC"));
        Assert.Equal(0m, DuplicateMath.Similarity("ABC", "XYZ"));
    }
}

public class PoRuleTests
{
    private static PoFacts Po(string status = "Open", string currency = "SAR", Guid? vendor = null, decimal prevValue = 0m, params PoLineFacts[] lines) =>
        new("PO-1", vendor ?? Rf.Vendor, currency, status, lines.Length == 0 ? [new PoLineFacts("1", 10m, 100m, 0m)] : lines, prevValue);

    private static IReadOnlyList<Finding> Match(VerificationInput i, Action<Dictionary<string, decimal>>? tweak = null) =>
        new PoMatchRule().Evaluate(i, Rf.Setting(RuleCodes.PoMatch, tweak));

    private static VerificationInput WithLines(PoFacts? po, params LineFacts[] lines) =>
        Rf.Input(Rf.Invoice(po: "PO-1", lines: lines, subtotal: lines.Sum(l => l.LineAmount)), po: po);

    [Fact]
    public void The_required_rule_fails_only_when_there_is_no_reference()
    {
        var rule = new PoRequiredRule();
        Assert.Single(rule.Evaluate(Rf.Input(Rf.Invoice(po: null)), Rf.Setting(RuleCodes.PoRequired)));
        Assert.Single(rule.Evaluate(Rf.Input(Rf.Invoice(po: "  ")), Rf.Setting(RuleCodes.PoRequired)));
        Assert.Empty(rule.Evaluate(Rf.Input(Rf.Invoice(po: "PO-1")), Rf.Setting(RuleCodes.PoRequired)));
    }

    [Fact]
    public void Without_a_reference_matching_has_nothing_to_do() => Assert.Empty(Match(Rf.Input(Rf.Invoice(po: null))));

    [Fact]
    public void An_unknown_purchase_order_is_reported() =>
        Assert.Equal("po_not_found", Assert.Single(Match(WithLines(null, Rf.Line(poLine: "1")))).Key);

    [Fact]
    public void A_clean_match_passes() => Assert.Empty(Match(WithLines(Po(), Rf.Line(qty: 5, price: 100m, poLine: "1"))));

    [Fact]
    public void The_order_must_belong_to_this_vendor_be_open_and_in_the_same_currency()
    {
        var input = WithLines(Po("Closed", "AED", Guid.NewGuid()), Rf.Line(poLine: "1"));
        var keys = Match(input).Select(f => f.Key).ToList();
        Assert.Contains("po_other_vendor", keys);
        Assert.Contains("po_currency_mismatch", keys);
        Assert.Contains("po_not_open", keys);
    }

    [Fact]
    public void Lines_need_a_valid_po_line()
    {
        Assert.Equal("line_without_po_line", Assert.Single(Match(WithLines(Po(), Rf.Line(poLine: null)))).Key);
        Assert.Equal("po_line_not_found", Assert.Single(Match(WithLines(Po(), Rf.Line(poLine: "9")))).Key);
    }

    [Theory]
    [InlineData(102.0, false)]   // exactly 2% above: allowed
    [InlineData(102.01, true)]
    [InlineData(98.0, false)]
    [InlineData(97.99, true)]
    public void Price_differences_are_judged_against_the_percentage_tolerance(double price, bool fails)
    {
        var findings = Match(WithLines(Po(), Rf.Line(qty: 1, price: (decimal)price, poLine: "1")));
        Assert.Equal(fails, findings.Any(f => f.Key == "po_price_mismatch"));
    }

    [Fact]
    public void The_absolute_price_tolerance_applies_when_it_is_larger_than_the_percentage()
    {
        var input = WithLines(Po(), Rf.Line(qty: 1, price: 105m, poLine: "1"));
        Assert.Contains(Match(input), f => f.Key == "po_price_mismatch");
        Assert.DoesNotContain(Match(input, p => p["priceToleranceAbsolute"] = 5m), f => f.Key == "po_price_mismatch");
        Assert.Contains(Match(input, p => p["priceToleranceAbsolute"] = 4.99m), f => f.Key == "po_price_mismatch");
    }

    [Fact]
    public void Quantity_cannot_exceed_the_order_taking_earlier_invoices_into_account()
    {
        var po = Po(lines: new PoLineFacts("1", 10m, 100m, 6m));
        Assert.DoesNotContain(Match(WithLines(po, Rf.Line(qty: 4, poLine: "1"))), f => f.Key == "po_quantity_exceeded");   // 6 + 4 = 10
        var over = Match(WithLines(po, Rf.Line(qty: 4.5m, poLine: "1")));
        Assert.Contains(over, f => f.Key == "po_quantity_exceeded");
        Assert.DoesNotContain(Match(WithLines(po, Rf.Line(qty: 4.5m, poLine: "1")), p => p["quantityTolerancePercent"] = 5m), f => f.Key == "po_quantity_exceeded");
    }

    [Fact]
    public void Two_lines_of_the_same_invoice_against_one_po_line_add_up()
    {
        var po = Po(lines: new PoLineFacts("1", 10m, 100m, 0m));
        var input = WithLines(po, Rf.Line(1, qty: 6, poLine: "1"), Rf.Line(2, qty: 6, poLine: "1"));
        var f = Assert.Single(Match(input), x => x.Key == "po_quantity_exceeded");
        Assert.Equal("2", f.Evidence["lineNo"]);
    }

    [Fact]
    public void The_total_value_invoiced_against_an_order_is_limited_by_over_billing()
    {
        var po = Po(prevValue: 800m, lines: new PoLineFacts("1", 10m, 100m, 0m)); // ordered 1000
        Assert.DoesNotContain(Match(WithLines(po, Rf.Line(qty: 2, price: 100m, poLine: "1"))), f => f.Key == "po_value_exceeded");   // 800 + 200
        Assert.Contains(Match(WithLines(po, Rf.Line(qty: 2.01m, price: 100m, poLine: "1"))), f => f.Key == "po_value_exceeded");
        Assert.DoesNotContain(Match(WithLines(po, Rf.Line(qty: 2.01m, price: 100m, poLine: "1")), p => p["overBillingPercent"] = 1m), f => f.Key == "po_value_exceeded");
    }
}

public class AmountRuleTests
{
    private static IReadOnlyList<Finding> Totals(InvoiceFacts i, Action<Dictionary<string, decimal>>? tweak = null) =>
        new AmountTotalsRule().Evaluate(Rf.Input(i), Rf.Setting(RuleCodes.AmountTotals, tweak));

    [Fact]
    public void A_consistent_invoice_passes() => Assert.Empty(Totals(Rf.Invoice()));

    [Fact]
    public void Subtotal_plus_tax_must_equal_the_total()
    {
        var f = Assert.Single(Totals(Rf.Invoice(total: 116m, subtotal: 100m, tax: 15m, lines: [Rf.Line()])));
        Assert.Equal("total_mismatch", f.Key);
    }

    [Theory]
    [InlineData(0.01, false)]   // one minor unit: inside the default tolerance
    [InlineData(0.02, true)]
    public void The_tolerance_is_in_minor_units_of_the_currency(double difference, bool fails)
    {
        var i = Rf.Invoice(total: 115m + (decimal)difference, subtotal: 100m, tax: 15m, lines: [Rf.Line()]);
        Assert.Equal(fails, Totals(i).Any(f => f.Key == "total_mismatch"));
    }

    [Fact]
    public void A_wider_tolerance_accepts_larger_rounding_differences() =>
        Assert.Empty(Totals(Rf.Invoice(total: 115.05m, subtotal: 100m, tax: 15m, lines: [Rf.Line()]), p => p["toleranceMinorUnits"] = 5m));

    [Fact]
    public void Zero_decimal_currencies_use_whole_units()
    {
        var line = Rf.Line(price: 1000m, tax: 150m);
        Assert.Empty(Totals(Rf.Invoice(total: 1150m, currency: "JPY", subtotal: 1000m, tax: 150m, lines: [line])));
        Assert.Contains(Totals(Rf.Invoice(total: 1152m, currency: "JPY", subtotal: 1000m, tax: 150m, lines: [line])), f => f.Key == "total_mismatch");
    }

    [Fact]
    public void Lines_must_add_up_to_the_subtotal_and_their_tax_to_the_tax_total()
    {
        var wrongLines = Rf.Invoice(total: 115m, subtotal: 100m, tax: 15m, lines: [Rf.Line(amount: 90m, price: 90m, tax: 15m)]);
        Assert.Contains(Totals(wrongLines), f => f.Key == "lines_subtotal_mismatch");
        var wrongTax = Rf.Invoice(total: 115m, subtotal: 100m, tax: 15m, lines: [Rf.Line(tax: 10m)]);
        Assert.Contains(Totals(wrongTax), f => f.Key == "lines_tax_mismatch");
    }

    [Fact]
    public void An_invoice_without_lines_is_checked_on_its_header_only()
    {
        Assert.Empty(Totals(Rf.Invoice(total: 115m, subtotal: 100m, tax: 15m, lines: [])));
        Assert.Contains(Totals(Rf.Invoice(total: 120m, subtotal: 100m, tax: 15m, lines: [])), f => f.Key == "total_mismatch");
    }

    [Fact]
    public void The_total_must_be_positive() =>
        Assert.Contains(Totals(Rf.Invoice(total: 0m, subtotal: 0m, tax: 0m, lines: [])), f => f.Key == "total_not_positive");

    [Fact]
    public void Line_amounts_must_equal_quantity_times_price()
    {
        var rule = new AmountLineMathRule();
        var ok = Rf.Input(Rf.Invoice(lines: [Rf.Line(qty: 3, price: 33.33m)]));
        Assert.Empty(rule.Evaluate(ok, Rf.Setting(RuleCodes.AmountLineMath)));
        var bad = Rf.Input(Rf.Invoice(lines: [Rf.Line(qty: 3, price: 33.33m, amount: 101m)]));
        var f = Assert.Single(rule.Evaluate(bad, Rf.Setting(RuleCodes.AmountLineMath)));
        Assert.Equal("99.9900", f.Evidence["expected"]);
    }

    [Fact]
    public void Line_tax_must_equal_the_amount_times_the_rate()
    {
        var rule = new TaxVatRule();
        Assert.Empty(rule.Evaluate(Rf.Input(), Rf.Setting(RuleCodes.TaxVat)));
        var wrong = Rf.Input(Rf.Invoice(lines: [Rf.Line(tax: 14m)]));
        Assert.Equal("line_tax_mismatch", Assert.Single(rule.Evaluate(wrong, Rf.Setting(RuleCodes.TaxVat))).Key);
        var rounding = Rf.Input(Rf.Invoice(lines: [Rf.Line(price: 33.33m, rate: 0.15m, tax: 5.00m)])); // 4.9995 rounds to 5.00
        Assert.Empty(rule.Evaluate(rounding, Rf.Setting(RuleCodes.TaxVat)));
    }
}

public class DateAndVendorRuleTests
{
    private static IReadOnlyList<Finding> Dates(InvoiceFacts i, Action<Dictionary<string, decimal>>? tweak = null) =>
        new DateSanityRule().Evaluate(Rf.Input(i), Rf.Setting(RuleCodes.DateSanity, tweak));

    [Fact]
    public void A_recent_invoice_passes() => Assert.Empty(Dates(Rf.Invoice(date: Rf.Today.AddDays(-30), due: Rf.Today.AddDays(30))));

    [Fact]
    public void Future_dated_invoices_fail_unless_allowed()
    {
        Assert.Equal("invoice_date_in_future", Assert.Single(Dates(Rf.Invoice(date: Rf.Today.AddDays(1)))).Key);
        Assert.Empty(Dates(Rf.Invoice(date: Rf.Today.AddDays(1)), p => p["maxFutureDays"] = 1m));
        Assert.Empty(Dates(Rf.Invoice(date: Rf.Today)));
    }

    [Fact]
    public void Stale_invoices_fail_after_the_configured_age()
    {
        Assert.Empty(Dates(Rf.Invoice(date: Rf.Today.AddDays(-365))));
        Assert.Equal("invoice_too_old", Assert.Single(Dates(Rf.Invoice(date: Rf.Today.AddDays(-366)))).Key);
        Assert.Empty(Dates(Rf.Invoice(date: Rf.Today.AddDays(-366)), p => p["maxAgeDays"] = 400m));
    }

    [Fact]
    public void The_due_date_cannot_precede_the_invoice_date()
    {
        Assert.Empty(Dates(Rf.Invoice(date: Rf.Today.AddDays(-5), due: Rf.Today.AddDays(-5))));
        Assert.Equal("due_before_invoice", Assert.Single(Dates(Rf.Invoice(date: Rf.Today.AddDays(-5), due: Rf.Today.AddDays(-6)))).Key);
    }

    [Fact]
    public void A_vendor_that_may_not_transact_always_fails()
    {
        var rule = new VendorStateRule();
        Assert.Empty(rule.Evaluate(Rf.Input(canTransact: true), Rf.Setting(RuleCodes.VendorState)));
        Assert.Equal("vendor_cannot_transact", Assert.Single(rule.Evaluate(Rf.Input(canTransact: false), Rf.Setting(RuleCodes.VendorState))).Key);
    }
}

public class VerificationEngineTests
{
    private static readonly VerificationEngine Engine = VerificationEngine.Standard();

    private static RuleResult Of(IReadOnlyList<RuleResult> results, string code) => results.Single(r => r.RuleCode == code);

    [Fact]
    public void Every_catalogue_rule_has_an_implementation_and_a_result()
    {
        var results = Engine.Run(Rf.Input(), RuleConfig.Defaults());
        Assert.Equal(RuleCatalog.All.Select(r => r.Code), results.Select(r => r.RuleCode));
    }

    [Fact]
    public void A_clean_invoice_fails_nothing_and_disabled_rules_are_skipped()
    {
        var results = Engine.Run(Rf.Input(Rf.Invoice(date: Rf.Today.AddDays(-3))), RuleConfig.Defaults());
        Assert.DoesNotContain(results, r => r.Failed);
        Assert.Equal(Outcome.Skipped, Of(results, RuleCodes.PoRequired).Outcome); // off by default
        Assert.Equal(Outcome.Passed, Of(results, RuleCodes.AmountTotals).Outcome);
    }

    [Fact]
    public void Severity_comes_from_the_entitys_configuration()
    {
        var input = Rf.Input(Rf.Invoice(date: Rf.Today.AddDays(-3)), [Rf.Candidate(date: Rf.Today.AddDays(-3))]);
        var strict = Of(Engine.Run(input, RuleConfig.Defaults()), RuleCodes.DuplicateExact);
        Assert.True(strict.Failed);
        Assert.Equal(RuleSeverity.Block, strict.Severity);

        var lax = RuleConfig.Normalize(new Dictionary<string, RuleSetting> { [RuleCodes.DuplicateExact] = new(true, RuleSeverity.Flag, new Dictionary<string, decimal>()) });
        Assert.Equal(RuleSeverity.Flag, Of(Engine.Run(input, lax), RuleCodes.DuplicateExact).Severity);
    }

    [Fact]
    public void The_same_invoice_gets_different_outcomes_under_different_entity_settings()
    {
        var input = Rf.Input(Rf.Invoice(date: Rf.Today.AddDays(-400)));
        var strictEntity = RuleConfig.Defaults();
        var laxEntity = RuleConfig.Normalize(new Dictionary<string, RuleSetting>
        {
            [RuleCodes.DateSanity] = new(true, RuleSeverity.Flag, new Dictionary<string, decimal> { ["maxAgeDays"] = 730m, ["maxFutureDays"] = 0m }),
        });
        Assert.True(Of(Engine.Run(input, strictEntity), RuleCodes.DateSanity).Failed);
        Assert.False(Of(Engine.Run(input, laxEntity), RuleCodes.DateSanity).Failed);
    }

    [Fact]
    public void A_disabled_rule_is_not_evaluated_even_when_it_would_fail()
    {
        var config = RuleConfig.Normalize(new Dictionary<string, RuleSetting> { [RuleCodes.AmountTotals] = new(false, RuleSeverity.Block, new Dictionary<string, decimal>()) });
        var bad = Rf.Input(Rf.Invoice(total: 999m, subtotal: 100m, tax: 15m, lines: []));
        Assert.Equal(Outcome.Skipped, Of(Engine.Run(bad, config), RuleCodes.AmountTotals).Outcome);
    }

    [Fact]
    public void The_engine_is_deterministic()
    {
        var input = Rf.Input(Rf.Invoice(date: Rf.Today.AddDays(-3)), [Rf.Candidate(date: Rf.Today.AddDays(-3))]);
        var a = Engine.Run(input, RuleConfig.Defaults());
        var b = Engine.Run(input, RuleConfig.Defaults());
        Assert.Equal(a.Select(r => (r.RuleCode, r.Outcome, r.Findings.Count)), b.Select(r => (r.RuleCode, r.Outcome, r.Findings.Count)));
    }
}
