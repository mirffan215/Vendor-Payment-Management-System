using System.Security.Cryptography;
using System.Text;

namespace Vpms.VendorPortal.Infrastructure;

internal static class Tokens
{
    /// <summary>256 random bits, URL-safe. Shown once; only its hash is stored.</summary>
    public static string NewInviteToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private const string RecoveryAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // no 0/O/1/I

    /// <summary>"K7QX2-M9PTR": 50 bits of entropy, single use.</summary>
    public static string NewRecoveryCode()
    {
        var chars = new char[10];
        for (var i = 0; i < chars.Length; i++) chars[i] = RecoveryAlphabet[RandomNumberGenerator.GetInt32(RecoveryAlphabet.Length)];
        return new string(chars, 0, 5) + "-" + new string(chars, 5, 5);
    }

    public static string NormalizeRecoveryCode(string value) => new(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    public static string HashRecoveryCode(string normalized) => Hash("recovery:" + normalized);
}
