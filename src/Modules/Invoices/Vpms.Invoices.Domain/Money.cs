namespace Vpms.Invoices.Domain;

/// <summary>Currency precision and rounding in one place, so every module rounds the same way.</summary>
public static class Money
{
    private static readonly Dictionary<string, int> Decimals = new(StringComparer.Ordinal)
    {
        ["JPY"] = 0, ["KRW"] = 0, ["VND"] = 0, ["CLP"] = 0, ["ISK"] = 0, ["UGX"] = 0,
        ["BHD"] = 3, ["KWD"] = 3, ["OMR"] = 3, ["JOD"] = 3, ["TND"] = 3, ["LYD"] = 3, ["IQD"] = 3,
    };

    public static int DecimalPlaces(string currencyCode) => Decimals.GetValueOrDefault(currencyCode, 2);

    /// <summary>Banker's rounding is wrong for tax; amounts round half away from zero.</summary>
    public static decimal Round(decimal amount, string currencyCode) =>
        Math.Round(amount, DecimalPlaces(currencyCode), MidpointRounding.AwayFromZero);

    /// <summary>One minor unit of the currency (0.01 for SAR).</summary>
    public static decimal MinorUnit(string currencyCode) => 1m / (decimal)Math.Pow(10, DecimalPlaces(currencyCode));
}

public static class InvoiceNumber
{
    /// <summary>Upper-case letters and digits only, so "INV-001", "inv 001" and "INV/0001" compare alike.</summary>
    public static string Normalize(string value) => new(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    /// <summary>Normalised and without leading zeros in digit runs, so "INV-0001" and "INV-1" match.</summary>
    public static string Canonical(string value) =>
        System.Text.RegularExpressions.Regex.Replace(Normalize(value), "(?<![0-9])0+(?=[0-9])", "");
}
