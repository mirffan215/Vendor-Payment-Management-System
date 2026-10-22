using System.Security.Cryptography;
using System.Text;

namespace Vpms.BuildingBlocks.Security;

/// <summary>RFC 4648 base32 (no padding), the format authenticator apps expect for TOTP secrets.</summary>
public static class Base32
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string Encode(ReadOnlySpan<byte> data)
    {
        var sb = new StringBuilder((data.Length * 8 + 4) / 5);
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                sb.Append(Alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }

        if (bits > 0) sb.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        return sb.ToString();
    }

    public static byte[] Decode(string text)
    {
        var clean = new string(text.Where(c => c is not ('=' or ' ' or '-')).Select(char.ToUpperInvariant).ToArray());
        var result = new List<byte>();
        int buffer = 0, bits = 0;
        foreach (var c in clean)
        {
            var value = Alphabet.IndexOf(c);
            if (value < 0) throw new FormatException("Invalid base32 character.");
            buffer = (buffer << 5) | value;
            bits += 5;
            if (bits >= 8)
            {
                result.Add((byte)((buffer >> (bits - 8)) & 0xFF));
                bits -= 8;
            }
        }

        return result.ToArray();
    }
}

/// <summary>Time-based one-time passwords (RFC 6238; HMAC-SHA-1, 6 digits, 30-second steps), as used by authenticator apps.</summary>
public static class Totp
{
    public const int StepSeconds = 30;
    public const int Digits = 6;

    public static string NewSecret() => Base32.Encode(RandomNumberGenerator.GetBytes(20));

    public static long StepFor(DateTimeOffset time) => time.ToUnixTimeSeconds() / StepSeconds;

    public static string Compute(string base32Secret, long step, int digits = Digits)
    {
        var key = Base32.Decode(base32Secret);
        var counter = new byte[8];
        for (var i = 7; i >= 0; i--, step >>= 8) counter[i] = (byte)(step & 0xFF);

        var hash = HMACSHA1.HashData(key, counter);
        var offset = hash[^1] & 0x0F;
        var binary = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (binary % (int)Math.Pow(10, digits)).ToString(new string('0', digits), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Returns the matching time step (within ±1 step for clock drift), or null. A code from a step at or before
    /// <paramref name="lastUsedStep"/> is refused, so each code works once.
    /// </summary>
    public static long? Verify(string base32Secret, string? code, DateTimeOffset now, long lastUsedStep)
    {
        if (code is null) return null;
        var cleaned = new string(code.Where(char.IsDigit).ToArray());
        if (cleaned.Length != Digits) return null;

        var current = StepFor(now);
        long? match = null;
        for (var step = current - 1; step <= current + 1; step++)
        {
            // Always evaluate every window and compare in constant time.
            var expected = Compute(base32Secret, step);
            if (CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(cleaned)) && step > lastUsedStep)
            {
                match = step;
            }
        }

        return match;
    }

    /// <summary>otpauth:// URI that authenticator apps read from a QR code.</summary>
    public static string ProvisioningUri(string issuer, string account, string base32Secret) =>
        $"otpauth://totp/{Uri.EscapeDataString(issuer)}:{Uri.EscapeDataString(account)}?secret={base32Secret}&issuer={Uri.EscapeDataString(issuer)}&algorithm=SHA1&digits={Digits}&period={StepSeconds}";
}
