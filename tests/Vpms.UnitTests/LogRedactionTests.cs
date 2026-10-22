using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Compact;
using Vpms.BuildingBlocks.Observability;

namespace Vpms.UnitTests;

public sealed class LogRedactionTests
{
    [Theory]
    [InlineData("paid to SA0380000000608010167519 today", "SA0380000000608010167519")]
    [InlineData("paid to SA03 8000 0000 6080 1016 7519 today", "6080 1016 7519")]
    [InlineData("GB82WEST12345698765432", "GB82WEST12345698765432")]
    [InlineData("account 123456789012345", "123456789012345")]
    [InlineData("Authorization: Bearer abcdefghijklmnop1234567890", "abcdefghijklmnop1234567890")]
    [InlineData("jwt eyJhbGciOiJSUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.c2lnbmF0dXJlMTIzNDU2", "eyJhbGciOiJSUzI1NiJ9")]
    [InlineData("callback ?code=1&client_secret=s3cr3t-value&state=x", "s3cr3t-value")]
    [InlineData("password=hunter2hunter2 failed", "hunter2hunter2")]
    public void Secrets_and_account_numbers_do_not_survive_scrubbing(string line, string secret)
    {
        Assert.DoesNotContain(secret, LogRedaction.Scrub(line), StringComparison.Ordinal);
    }

    [Fact]
    public void Account_numbers_keep_their_last_four_digits_so_a_support_person_can_recognise_them()
    {
        Assert.Equal("account ****2345", LogRedaction.Scrub("account 123456789012345"));
        Assert.Contains("****7519", LogRedaction.Scrub("SA0380000000608010167519"));
    }

    [Fact]
    public void Email_addresses_are_partly_masked()
    {
        Assert.Equal("a***@acme.com wrote", LogRedaction.Scrub("alice@acme.com wrote"));
    }

    [Theory]
    [InlineData("invoice 2026-10-22 total 1150.00 SAR")]
    [InlineData("took 123456 ms, sequence 98765432")]
    [InlineData("port 5432 user 42")]
    [InlineData("amount 1234567890123.45")]
    public void Ordinary_numbers_and_text_are_left_alone(string line) => Assert.Equal(line, LogRedaction.Scrub(line));

    [Theory]
    [InlineData("Password")]
    [InlineData("ClientSecret")]
    [InlineData("accessToken")]
    [InlineData("AuthorizationHeader")]
    [InlineData("Iban")]
    [InlineData("AccountNumber")]
    [InlineData("connectionString")]
    public void Properties_named_like_secrets_are_replaced(string name) => Assert.True(LogRedaction.IsSensitiveName(name));

    [Theory]
    [InlineData("TenantId")]
    [InlineData("UserId")]
    [InlineData("InvoiceNumber")]
    [InlineData("StatusCode")]
    public void Ordinary_property_names_are_kept(string name) => Assert.False(LogRedaction.IsSensitiveName(name));

    [Fact]
    public void A_logged_secret_property_and_a_secret_inside_the_message_both_disappear_from_the_written_line()
    {
        var events = new List<LogEvent>();
        using var logger = new LoggerConfiguration().Enrich.With<RedactingEnricher>().WriteTo.Sink(new Capture(events)).CreateLogger();
        logger.Information("Sending to {Iban} with {Password} for {Email}", "SA0380000000608010167519", "hunter2", "bob@acme.com");

        var written = new StringWriter();
        new ScrubbingFormatter(new RenderedCompactJsonFormatter()).Format(events.Single(), written);
        var line = written.ToString();
        Assert.DoesNotContain("SA0380000000608010167519", line, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", line, StringComparison.Ordinal);
        Assert.DoesNotContain("bob@acme.com", line, StringComparison.Ordinal);
        Assert.Contains("[redacted]", line, StringComparison.Ordinal);
    }

    [Fact]
    public void An_exception_message_is_scrubbed_too()
    {
        var events = new List<LogEvent>();
        using var logger = new LoggerConfiguration().WriteTo.Sink(new Capture(events)).CreateLogger();
        logger.Error(new InvalidOperationException("login failed for token=abc123def456ghi789"), "boom");
        var written = new StringWriter();
        new ScrubbingFormatter(new RenderedCompactJsonFormatter()).Format(events.Single(), written);
        Assert.DoesNotContain("abc123def456ghi789", written.ToString(), StringComparison.Ordinal);
    }

    private sealed class Capture(List<LogEvent> events) : ILogEventSink
    {
        public void Emit(LogEvent logEvent) => events.Add(logEvent);
    }
}
