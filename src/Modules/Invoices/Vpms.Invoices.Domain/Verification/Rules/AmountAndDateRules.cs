using System.Globalization;

namespace Vpms.Invoices.Domain.Verification.Rules;

internal static class Tolerance
{
    public static decimal Of(string currency, decimal minorUnits) => Money.MinorUnit(currency) * minorUnits;

    public static string F(decimal value) => value.ToString("F4", CultureInfo.InvariantCulture);
}

/// <summary>The numbers add up: lines to subtotal, subtotal plus tax to total, line tax to tax total.</summary>
public sealed class AmountTotalsRule : IVerificationRule
{
    public string Code => RuleCodes.AmountTotals;

    public IReadOnlyList<Finding> Evaluate(VerificationInput input, RuleSetting setting)
    {
        var i = input.Invoice;
        var tolerance = Tolerance.Of(i.CurrencyCode, setting.Parameters["toleranceMinorUnits"]);
        var findings = new List<Finding>();

        if (Math.Abs(i.Subtotal + i.TaxTotal - i.Total) > tolerance)
        {
            findings.Add(Finding.Of("total_mismatch", ("subtotal", Tolerance.F(i.Subtotal)), ("tax", Tolerance.F(i.TaxTotal)), ("total", Tolerance.F(i.Total))));
        }

        if (i.Lines.Count > 0)
        {
            var linesTotal = i.Lines.Sum(l => l.LineAmount);
            if (Math.Abs(linesTotal - i.Subtotal) > tolerance)
            {
                findings.Add(Finding.Of("lines_subtotal_mismatch", ("lines", Tolerance.F(linesTotal)), ("subtotal", Tolerance.F(i.Subtotal))));
            }

            var linesTax = i.Lines.Sum(l => l.TaxAmount);
            if (Math.Abs(linesTax - i.TaxTotal) > tolerance)
            {
                findings.Add(Finding.Of("lines_tax_mismatch", ("lines", Tolerance.F(linesTax)), ("tax", Tolerance.F(i.TaxTotal))));
            }
        }

        if (i.Total <= 0) findings.Add(Finding.Of("total_not_positive", ("total", Tolerance.F(i.Total))));
        return findings;
    }
}

/// <summary>Each line: quantity times price equals the line amount.</summary>
public sealed class AmountLineMathRule : IVerificationRule
{
    public string Code => RuleCodes.AmountLineMath;

    public IReadOnlyList<Finding> Evaluate(VerificationInput input, RuleSetting setting)
    {
        var i = input.Invoice;
        var tolerance = Tolerance.Of(i.CurrencyCode, setting.Parameters["toleranceMinorUnits"]);
        return i.Lines.Where(l => Math.Abs(Money.Round(l.Quantity * l.UnitPrice, i.CurrencyCode) - l.LineAmount) > tolerance)
            .Select(l => Finding.Of("line_amount_mismatch", ("lineNo", l.LineNo.ToString(CultureInfo.InvariantCulture)),
                ("expected", Tolerance.F(Money.Round(l.Quantity * l.UnitPrice, i.CurrencyCode))), ("actual", Tolerance.F(l.LineAmount)))).ToList();
    }
}

/// <summary>VAT arithmetic on each line (amount times rate equals tax). Country-specific checks join this rule in the tax module.</summary>
public sealed class TaxVatRule : IVerificationRule
{
    public string Code => RuleCodes.TaxVat;

    public IReadOnlyList<Finding> Evaluate(VerificationInput input, RuleSetting setting)
    {
        var i = input.Invoice;
        var tolerance = Tolerance.Of(i.CurrencyCode, setting.Parameters["toleranceMinorUnits"]);
        var findings = i.Lines.Where(l => Math.Abs(Money.Round(l.LineAmount * l.TaxRate, i.CurrencyCode) - l.TaxAmount) > tolerance)
            .Select(l => Finding.Of("line_tax_mismatch", ("lineNo", l.LineNo.ToString(CultureInfo.InvariantCulture)),
                ("expected", Tolerance.F(Money.Round(l.LineAmount * l.TaxRate, i.CurrencyCode))), ("actual", Tolerance.F(l.TaxAmount)))).ToList();

        // The country's own checks, for the switches the entity has on. Isolated: with no provider only the arithmetic above runs.
        if (input.TaxProvider is { } provider)
        {
            findings.AddRange(provider.Evaluate(new Tax.TaxInput(i, input.VendorTaxNumber, setting.Parameters)));
        }

        return findings;
    }
}

public sealed class DateSanityRule : IVerificationRule
{
    public string Code => RuleCodes.DateSanity;

    public IReadOnlyList<Finding> Evaluate(VerificationInput input, RuleSetting setting)
    {
        var i = input.Invoice;
        var maxAge = (int)setting.Parameters["maxAgeDays"];
        var maxFuture = (int)setting.Parameters["maxFutureDays"];
        var findings = new List<Finding>();

        var age = input.Today.DayNumber - i.InvoiceDate.DayNumber;
        if (age < -maxFuture) findings.Add(Finding.Of("invoice_date_in_future", ("invoiceDate", i.InvoiceDate.ToString("yyyy-MM-dd")), ("daysAhead", (-age).ToString(CultureInfo.InvariantCulture))));
        if (age > maxAge) findings.Add(Finding.Of("invoice_too_old", ("invoiceDate", i.InvoiceDate.ToString("yyyy-MM-dd")), ("ageDays", age.ToString(CultureInfo.InvariantCulture))));
        if (i.DueDate is { } due && due < i.InvoiceDate) findings.Add(Finding.Of("due_before_invoice", ("invoiceDate", i.InvoiceDate.ToString("yyyy-MM-dd")), ("dueDate", due.ToString("yyyy-MM-dd"))));
        return findings;
    }
}

public sealed class VendorStateRule : IVerificationRule
{
    public string Code => RuleCodes.VendorState;

    public IReadOnlyList<Finding> Evaluate(VerificationInput input, RuleSetting setting) =>
        input.Vendor.CanTransact ? [] : [Finding.Of("vendor_cannot_transact")];
}
