using System.Numerics;

namespace Vpms.Vendors.Domain;

/// <summary>IBAN checks (ISO 13616): length per country and the mod-97 checksum.</summary>
public static class Iban
{
    private static readonly IReadOnlyDictionary<string, int> Lengths = new Dictionary<string, int>
    {
        ["SA"] = 24, ["AE"] = 23, ["BH"] = 22, ["KW"] = 30, ["QA"] = 29, ["OM"] = 23, ["JO"] = 30, ["EG"] = 29, ["LB"] = 28,
        ["GB"] = 22, ["DE"] = 22, ["FR"] = 27, ["ES"] = 24, ["IT"] = 27, ["NL"] = 18, ["BE"] = 16, ["CH"] = 21, ["AT"] = 20,
        ["IE"] = 22, ["PT"] = 25, ["TR"] = 26, ["PK"] = 24,
    };

    /// <summary>Countries where an IBAN is the normal way to address an account, so it is required.</summary>
    public static bool IsRequiredFor(string countryCode) => Lengths.ContainsKey(countryCode);

    public static string Normalize(string value) => new(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    public static bool IsValid(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var iban = Normalize(value);
        if (iban.Length is < 15 or > 34 || !char.IsLetter(iban[0]) || !char.IsLetter(iban[1]) || !char.IsDigit(iban[2]) || !char.IsDigit(iban[3]))
        {
            return false;
        }

        if (Lengths.TryGetValue(iban[..2], out var expected) && iban.Length != expected)
        {
            return false;
        }

        var rearranged = iban[4..] + iban[..4];
        var digits = new System.Text.StringBuilder();
        foreach (var c in rearranged)
        {
            if (char.IsDigit(c)) digits.Append(c);
            else if (c is >= 'A' and <= 'Z') digits.Append(c - 'A' + 10);
            else return false;
        }

        return BigInteger.Parse(digits.ToString()) % 97 == 1;
    }

    public static string Mask(string normalized) => "••••" + normalized[^4..];
}
