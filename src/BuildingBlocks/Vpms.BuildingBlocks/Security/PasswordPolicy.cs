namespace Vpms.BuildingBlocks.Security;

/// <summary>
/// Password rules in the spirit of NIST SP 800-63B / OWASP ASVS: length over composition, no common or personal passwords.
/// Returns a stable reason code, never the password.
/// </summary>
public static class PasswordPolicy
{
    public const int MinLength = 12;
    public const int MaxLength = 128;

    private static readonly HashSet<string> Common = new(StringComparer.OrdinalIgnoreCase)
    {
        "password", "password1", "password123", "passw0rd", "123456", "12345678", "123456789", "1234567890", "qwerty", "qwertyuiop",
        "qwerty123", "abc123", "letmein", "welcome", "welcome1", "admin", "administrator", "iloveyou", "monkey", "dragon", "football",
        "baseball", "sunshine", "princess", "master", "shadow", "superman", "trustno1", "changeme", "default", "login", "passw0rd1",
        "p@ssw0rd", "p@ssword", "pa$$word", "000000", "111111", "121212", "123123", "654321", "666666", "696969", "7777777", "987654321",
        "zaq12wsx", "1q2w3e4r", "1qaz2wsx", "q1w2e3r4", "google", "vendor", "supplier", "company", "payment", "payments", "invoice",
    };

    /// <returns>null when acceptable; otherwise a stable code such as "too_short".</returns>
    public static string? Check(string? password, params string?[] personalData)
    {
        if (string.IsNullOrEmpty(password)) return "too_short";
        if (password.Length < MinLength) return "too_short";
        if (password.Length > MaxLength) return "too_long";
        if (password.Trim().Length != password.Length && password.Trim().Length < MinLength) return "too_short";

        var lower = password.ToLowerInvariant();
        // "Password123456!" is still "password": strip trailing digits and symbols before looking it up.
        var stem = lower.Replace(" ", "").TrimEnd([.. "0123456789!@#$%^&*()-_=+.?"]);
        if (Common.Contains(lower) || Common.Contains(stem)) return "too_common";
        if (password.Distinct().Count() < 5 || IsRepeatedUnit(lower)) return "too_repetitive";
        if (IsSequential(lower)) return "too_sequential";

        foreach (var item in personalData)
        {
            if (string.IsNullOrWhiteSpace(item)) continue;
            foreach (var token in item.Split(['@', '.', ' ', '-', '_'], StringSplitOptions.RemoveEmptyEntries))
            {
                if (token.Length >= 4 && lower.Contains(token.ToLowerInvariant(), StringComparison.Ordinal)) return "contains_personal_data";
            }
        }

        return null;
    }

    /// <summary>"passwordpassword", "abcabcabcabc": the whole password is one short unit repeated.</summary>
    private static bool IsRepeatedUnit(string s)
    {
        for (var unit = 1; unit <= s.Length / 2; unit++)
        {
            if (s.Length % unit != 0) continue;
            var repeated = true;
            for (var i = unit; i < s.Length && repeated; i++) repeated = s[i] == s[i - unit];
            if (repeated) return true;
        }

        return false;
    }

    private static bool IsSequential(string s)
    {
        var steps = 0;
        for (var i = 1; i < s.Length; i++)
        {
            if (Math.Abs(s[i] - s[i - 1]) == 1) steps++;
        }

        return steps >= s.Length - 2;
    }
}
