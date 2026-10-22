using System.Text;
using Vpms.BuildingBlocks.Security;

namespace Vpms.UnitTests.Security;

public class TotpAndPasswordTests
{
    // RFC 6238 appendix B: SHA-1 secret "12345678901234567890"
    private static readonly string Secret = Base32.Encode(Encoding.ASCII.GetBytes("12345678901234567890"));

    [Theory]
    [InlineData(59L, "94287082")]
    [InlineData(1111111109L, "07081804")]
    [InlineData(1111111111L, "14050471")]
    [InlineData(1234567890L, "89005924")]
    [InlineData(2000000000L, "69279037")]
    public void Matches_the_rfc_6238_test_vectors(long unixTime, string expected8Digits) =>
        Assert.Equal(expected8Digits, Totp.Compute(Secret, unixTime / 30, 8));

    [Fact]
    public void Six_digit_codes_are_the_tail_of_the_eight_digit_ones() =>
        Assert.Equal("287082", Totp.Compute(Secret, 59 / 30));

    [Fact]
    public void Base32_round_trips_and_ignores_formatting()
    {
        var data = Enumerable.Range(0, 40).Select(i => (byte)(i * 7)).ToArray();
        Assert.Equal(data, Base32.Decode(Base32.Encode(data)));
        Assert.Equal(Encoding.ASCII.GetBytes("foobar"), Base32.Decode("mzxw 6ytb-oi======"));
        Assert.Throws<FormatException>(() => Base32.Decode("not base32!"));
    }

    [Fact]
    public void Accepts_the_current_and_neighbouring_steps_only()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        var step = Totp.StepFor(now);
        Assert.Equal(step, Totp.Verify(Secret, Totp.Compute(Secret, step), now, 0));
        Assert.Equal(step - 1, Totp.Verify(Secret, Totp.Compute(Secret, step - 1), now, 0));
        Assert.Equal(step + 1, Totp.Verify(Secret, Totp.Compute(Secret, step + 1), now, 0));
        Assert.Null(Totp.Verify(Secret, Totp.Compute(Secret, step - 2), now, 0));
        Assert.Null(Totp.Verify(Secret, Totp.Compute(Secret, step + 2), now, 0));
    }

    [Fact]
    public void A_code_cannot_be_used_twice()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        var step = Totp.StepFor(now);
        var code = Totp.Compute(Secret, step);
        var used = Totp.Verify(Secret, code, now, 0)!.Value;
        Assert.Null(Totp.Verify(Secret, code, now, used));
        Assert.Null(Totp.Verify(Secret, Totp.Compute(Secret, step - 1), now, used)); // an older code is refused too
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("12345")]
    [InlineData("1234567")]
    [InlineData("abcdef")]
    public void Malformed_codes_never_verify(string? code) =>
        Assert.Null(Totp.Verify(Secret, code, DateTimeOffset.UtcNow, 0));

    [Fact]
    public void Formatting_in_the_code_is_tolerated()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        var code = Totp.Compute(Secret, Totp.StepFor(now));
        Assert.NotNull(Totp.Verify(Secret, code[..3] + " " + code[3..], now, 0));
    }

    [Fact]
    public void Provisioning_uri_has_what_authenticator_apps_need()
    {
        var uri = Totp.ProvisioningUri("VPMS", "ap@vendor.example", "JBSWY3DPEHPK3PXP");
        Assert.StartsWith("otpauth://totp/VPMS:ap%40vendor.example?secret=JBSWY3DPEHPK3PXP", uri);
        Assert.Contains("digits=6", uri);
        Assert.Contains("period=30", uri);
    }

    [Theory]
    [InlineData("correct horse battery staple", null)]
    [InlineData("Tr0ub4dor&3-extended", null)]
    [InlineData("short1!", "too_short")]
    [InlineData("", "too_short")]
    [InlineData("password123456", "too_common")]
    [InlineData("Welcome1234567!", "too_common")]
    [InlineData("passwordpassword", "too_repetitive")]
    [InlineData("aaaaaaaaaaaaaaaa", "too_repetitive")]
    [InlineData("abcdefghijklmnop", "too_sequential")]
    [InlineData("123456789012", "too_sequential")]
    public void Checks_password_quality(string password, string? expected) =>
        Assert.Equal(expected, PasswordPolicy.Check(password));

    [Theory]
    [InlineData("1234567890", "too_short")]
    [InlineData("Password123", "too_short")]
    public void Common_and_short_passwords_are_refused(string password, string expected) =>
        Assert.Equal(expected, PasswordPolicy.Check(password));

    [Fact]
    public void Personal_data_may_not_appear_in_the_password()
    {
        Assert.Equal("contains_personal_data", PasswordPolicy.Check("alice.smith-is-great-1", "alice.smith@vendor.example"));
        Assert.Equal("contains_personal_data", PasswordPolicy.Check("My Gulf Parts secret!", null, "Gulf Parts Co"));
        Assert.Null(PasswordPolicy.Check("purple elephants dance quietly", "alice@vendor.example"));
    }

    [Fact]
    public void Very_long_passwords_are_refused_to_avoid_hash_dos() =>
        Assert.Equal("too_long", PasswordPolicy.Check(new string('x', 129) + "yzabc"));
}
