using System.Text.RegularExpressions;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting;

namespace Vpms.BuildingBlocks.Observability;

/// <summary>
/// Keeps secrets and payment data out of logs even when a developer logs more than they meant to. Properties whose names suggest a secret are
/// replaced, and the rendered line is scrubbed for account numbers, tokens, and e-mail addresses. It is a safety net, not permission to log such data.
/// </summary>
public static partial class LogRedaction
{
    public const string Redacted = "[redacted]";

    private static readonly string[] SensitiveNames =
    [
        "password", "passwd", "secret", "token", "authorization", "cookie", "apikey", "api_key", "iban", "accountnumber", "account_number", "bankaccount",
        "recoverycode", "otp", "totp", "privatekey", "connectionstring", "sas", "csrf",
    ];

    public static bool IsSensitiveName(string name) => SensitiveNames.Any(s => name.Contains(s, StringComparison.OrdinalIgnoreCase));

    public static string Scrub(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        text = Jwt().Replace(text, "[jwt]");
        text = Bearer().Replace(text, "Bearer " + Redacted);
        text = Assignment().Replace(text, m => m.Groups[1].Value + "=" + Redacted);
        text = Iban().Replace(text, m => MaskTail(m.Value.Replace(" ", "", StringComparison.Ordinal)));
        text = Email().Replace(text, m => m.Groups[1].Value + "***@" + m.Groups[2].Value);
        text = Digits().Replace(text, m => MaskTail(m.Value));
        return text;
    }

    /// <summary>"****1234": enough to recognise an account, not enough to use it.</summary>
    public static string MaskTail(string value) => value.Length <= 4 ? new string('*', value.Length) : "****" + value[^4..];

    [GeneratedRegex(@"eyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}")]
    private static partial Regex Jwt();

    [GeneratedRegex(@"(?i)\bbearer\s+[A-Za-z0-9._~+/=-]{8,}")]
    private static partial Regex Bearer();

    [GeneratedRegex(@"(?i)\b(password|passwd|secret|client_secret|apikey|api_key|token|access_token|refresh_token|id_token|sig)=([^&\s""',;]+)")]
    private static partial Regex Assignment();

    [GeneratedRegex(@"\b[A-Z]{2}\d{2}(?: ?[A-Z0-9]{4}){3,7}(?: ?[A-Z0-9]{1,3})?\b")]
    private static partial Regex Iban();

    [GeneratedRegex(@"\b([A-Za-z0-9._%+-])[A-Za-z0-9._%+-]*@([A-Za-z0-9.-]+\.[A-Za-z]{2,})\b")]
    private static partial Regex Email();

    // Long digit runs are account or card numbers. Shorter numbers (counts, sequences, ports, years) stay readable.
    [GeneratedRegex(@"(?<![\d.])\d{12,34}(?![\d.])")]
    private static partial Regex Digits();
}

/// <summary>Replaces the value of properties named like secrets before any sink sees them.</summary>
public sealed class RedactingEnricher : ILogEventEnricher
{
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        foreach (var property in logEvent.Properties.Where(p => LogRedaction.IsSensitiveName(p.Key)).ToList())
        {
            logEvent.AddOrUpdateProperty(new LogEventProperty(property.Key, new ScalarValue(LogRedaction.Redacted)));
        }
    }
}

/// <summary>Writes the inner format, then scrubs the finished line (message text, properties and exception text alike).</summary>
public sealed class ScrubbingFormatter(ITextFormatter inner) : ITextFormatter
{
    public void Format(LogEvent logEvent, TextWriter output)
    {
        using var buffer = new StringWriter();
        inner.Format(logEvent, buffer);
        output.Write(LogRedaction.Scrub(buffer.ToString()));
    }
}
