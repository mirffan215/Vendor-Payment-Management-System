namespace Vpms.Invoices.Domain.Verification.Rules;

public static class DuplicateMath
{
    public static bool IsExact(InvoiceFacts i, CandidateFacts c) =>
        c.CanonicalNumber == i.CanonicalNumber && c.InvoiceDate == i.InvoiceDate
        && Money.Round(c.Total, i.CurrencyCode) == Money.Round(i.Total, i.CurrencyCode) && c.CurrencyCode == i.CurrencyCode;

    public static bool IsSameNumber(InvoiceFacts i, CandidateFacts c) => c.CanonicalNumber == i.CanonicalNumber;

    /// <summary>1 for identical strings, 0 for nothing in common (edit distance relative to the longer string).</summary>
    public static decimal Similarity(string a, string b)
    {
        if (a.Length == 0 && b.Length == 0) return 1m;
        var longest = Math.Max(a.Length, b.Length);
        return 1m - (decimal)Levenshtein(a, b) / longest;
    }

    public static int Levenshtein(string a, string b)
    {
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) previous[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }
}

/// <summary>The same vendor, number, amount and date: the same invoice sent twice.</summary>
public sealed class DuplicateExactRule : IVerificationRule
{
    public string Code => RuleCodes.DuplicateExact;

    public IReadOnlyList<Finding> Evaluate(VerificationInput input, RuleSetting setting)
    {
        if (input.Invoice.CanonicalNumber.Length == 0) return [];
        return input.Candidates.Where(c => DuplicateMath.IsExact(input.Invoice, c))
            .Select(c => Finding.Of("duplicate_exact", ("invoiceId", c.InvoiceId.ToString()), ("invoiceNumber", c.InvoiceNumber), ("status", c.Status))).ToList();
    }
}

/// <summary>The same number again with a different amount or date: a corrected invoice, or a reused number. A person decides.</summary>
public sealed class DuplicateSameNumberRule : IVerificationRule
{
    public string Code => RuleCodes.DuplicateSameNumber;

    public IReadOnlyList<Finding> Evaluate(VerificationInput input, RuleSetting setting)
    {
        if (input.Invoice.CanonicalNumber.Length == 0) return [];
        return input.Candidates.Where(c => DuplicateMath.IsSameNumber(input.Invoice, c) && !DuplicateMath.IsExact(input.Invoice, c))
            .Select(c => Finding.Of("duplicate_same_number", ("invoiceId", c.InvoiceId.ToString()), ("invoiceNumber", c.InvoiceNumber), ("total", c.Total.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)),
                ("invoiceDate", c.InvoiceDate.ToString("yyyy-MM-dd")))).ToList();
    }
}

/// <summary>A near miss: a very similar number with nearly the same amount at nearly the same time ("INV-1001" vs "INV-1010").</summary>
public sealed class DuplicateFuzzyRule : IVerificationRule
{
    public string Code => RuleCodes.DuplicateFuzzy;

    public IReadOnlyList<Finding> Evaluate(VerificationInput input, RuleSetting setting)
    {
        var i = input.Invoice;
        if (i.CanonicalNumber.Length == 0 || i.Total <= 0) return [];
        var amountPercent = setting.Parameters["amountTolerancePercent"];
        var windowDays = (int)setting.Parameters["dateWindowDays"];
        var minSimilarity = setting.Parameters["numberSimilarity"];

        var findings = new List<Finding>();
        foreach (var c in input.Candidates)
        {
            if (DuplicateMath.IsExact(i, c) || DuplicateMath.IsSameNumber(i, c) || c.CurrencyCode != i.CurrencyCode) continue;
            var amountDiff = Math.Abs(c.Total - i.Total);
            if (amountDiff > i.Total * amountPercent / 100m) continue;
            if (Math.Abs(c.InvoiceDate.DayNumber - i.InvoiceDate.DayNumber) > windowDays) continue;
            var similarity = DuplicateMath.Similarity(i.CanonicalNumber, c.CanonicalNumber);
            if (similarity < minSimilarity) continue;

            findings.Add(Finding.Of("duplicate_similar", ("invoiceId", c.InvoiceId.ToString()), ("invoiceNumber", c.InvoiceNumber),
                ("total", c.Total.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)), ("invoiceDate", c.InvoiceDate.ToString("yyyy-MM-dd")),
                ("similarity", similarity.ToString("F2", System.Globalization.CultureInfo.InvariantCulture))));
        }

        return findings;
    }
}
