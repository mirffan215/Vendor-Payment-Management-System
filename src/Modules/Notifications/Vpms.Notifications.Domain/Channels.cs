using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using Vpms.BuildingBlocks.Errors;
using Vpms.BuildingBlocks.Security;
using Vpms.BuildingBlocks.Tenancy;

namespace Vpms.Notifications.Domain;

public static class DeliveryStatus
{
    public const string Pending = "Pending";
    public const string InFlight = "InFlight";
    public const string Sent = "Sent";

    /// <summary>Not sent for a reason that is not a failure (the person has no email address, or is no longer active).</summary>
    public const string Skipped = "Skipped";

    public const string DeadLetter = "DeadLetter";
}

/// <summary>One notification to one outside channel. Like the ERP queue: leased, retried with backoff, dead-lettered when it cannot be sent.</summary>
public class NotificationDelivery : ITenantOwned
{
    public const int MaxAttempts = 6;
    public const int LeaseMinutes = 5;

    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid NotificationId { get; set; }
    public Guid UserId { get; set; }
    public string Channel { get; set; } = NotificationChannel.Email;
    public string Status { get; set; } = DeliveryStatus.Pending;
    public int Attempts { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public DateTimeOffset? LeaseUntil { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? SentAt { get; set; }

    [System.ComponentModel.DataAnnotations.Timestamp]
    public byte[] RowVersion { get; set; } = [];

    public bool IsDue(DateTimeOffset now) => (Status == DeliveryStatus.Pending && NextAttemptAt <= now) || (Status == DeliveryStatus.InFlight && LeaseUntil <= now);

    public void Lease(DateTimeOffset now)
    {
        if (!IsDue(now)) throw new ConflictException("not_due", "This delivery is not ready.");
        Status = DeliveryStatus.InFlight;
        LeaseUntil = now.AddMinutes(LeaseMinutes);
        Attempts++;
    }

    public void MarkSent(DateTimeOffset now)
    {
        Status = DeliveryStatus.Sent;
        SentAt = now;
        LastError = null;
        LeaseUntil = null;
    }

    public void Skip(string reason)
    {
        Status = DeliveryStatus.Skipped;
        LastError = Cut(reason);
        LeaseUntil = null;
    }

    /// <summary>Waits and tries again with a doubling delay (one minute, two, four...), or gives up after the last attempt.</summary>
    public void FailTransient(string code, DateTimeOffset now, TimeSpan? retryAfter)
    {
        LastError = Cut(code);
        LeaseUntil = null;
        if (Attempts >= MaxAttempts)
        {
            Status = DeliveryStatus.DeadLetter;
            LastError = Cut($"Gave up after {Attempts} attempts: {code}");
            return;
        }

        var delay = TimeSpan.FromMinutes(Math.Pow(2, Attempts - 1));
        if (retryAfter is { } r && r > delay) delay = r;
        Status = DeliveryStatus.Pending;
        NextAttemptAt = now + (delay > TimeSpan.FromHours(1) ? TimeSpan.FromHours(1) : delay);
    }

    public void FailPermanent(string code)
    {
        Status = DeliveryStatus.DeadLetter;
        LastError = Cut(code);
        LeaseUntil = null;
    }

    private static string Cut(string text) => text.Length <= 500 ? text : text[..500];
}

public static class ChannelConfigStatus
{
    public const string Pending = "Pending";
    public const string Active = "Active";
    public const string Superseded = "Superseded";
    public const string Rejected = "Rejected";
    public const string Cancelled = "Cancelled";
}

/// <summary>
/// One version of how a tenant sends on a channel (which mailbox, which webhook). It decides where messages about money and approvals go,
/// so each version waits for a different person to approve it. Only the name of a secret is stored.
/// </summary>
public class ChannelConfig : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string Channel { get; set; } = NotificationChannel.Email;
    public int VersionNo { get; set; }
    public string ConfigJson { get; set; } = "{}";
    public string Status { get; set; } = ChannelConfigStatus.Pending;
    public string Note { get; set; } = "";
    public Guid CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Guid? DecidedBy { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    public string? DecisionComment { get; set; }

    [System.ComponentModel.DataAnnotations.Timestamp]
    public byte[] RowVersion { get; set; } = [];

    public void Approve(Guid checker, string? comment, DateTimeOffset now)
    {
        RequirePending();
        MakerCheckerGuard.EnsureDifferent(CreatedBy, checker, "approve a notification channel change");
        Status = ChannelConfigStatus.Active;
        DecidedBy = checker;
        DecidedAt = now;
        DecisionComment = Clean(comment);
    }

    public void Reject(Guid checker, string comment, DateTimeOffset now)
    {
        RequirePending();
        MakerCheckerGuard.EnsureDifferent(CreatedBy, checker, "reject a notification channel change");
        if (string.IsNullOrWhiteSpace(comment)) throw new ValidationFailedException("reason_required", "A reason is required.");
        Status = ChannelConfigStatus.Rejected;
        DecidedBy = checker;
        DecidedAt = now;
        DecisionComment = Clean(comment);
    }

    public void Cancel(Guid actor, DateTimeOffset now)
    {
        RequirePending();
        if (actor != CreatedBy) throw new ConflictException("not_the_maker", "Only the person who made the change can cancel it.");
        Status = ChannelConfigStatus.Cancelled;
        DecidedAt = now;
    }

    public void Supersede()
    {
        if (Status != ChannelConfigStatus.Active) throw new ConflictException("invalid_transition", "Only the active version can be superseded.");
        Status = ChannelConfigStatus.Superseded;
    }

    private void RequirePending()
    {
        if (Status != ChannelConfigStatus.Pending) throw new ConflictException("not_pending", "This change has already been decided.");
    }

    private static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim()[..Math.Min(text.Trim().Length, 500)];
}

public sealed record ConfigIssue(string Code, string Message);

public sealed record EmailChannelConfig(
    string Provider, string FromAddress, string? FromName, string? GraphTenantId, string? GraphClientId, string? SecretRef, string? Mailbox, string? SmtpHost, int SmtpPort, bool SmtpStartTls, string? SmtpUsername)
{
    public const string Graph = "Graph";
    public const string Smtp = "Smtp";
    private static readonly Regex Address = new(@"^[^\s@<>,;""]+@[^\s@<>,;""]+\.[^\s@<>,;""]+$", RegexOptions.Compiled);

    public IReadOnlyList<ConfigIssue> Validate(bool allowInternalHosts)
    {
        var issues = new List<ConfigIssue>();
        if (Provider is not (Graph or Smtp)) issues.Add(new("provider_unknown", "Unknown email provider."));
        if (!Address.IsMatch(FromAddress ?? "") || FromAddress!.Length > 254) issues.Add(new("from_invalid", "The sender address is not valid."));
        if (FromName is { Length: > 100 } || (FromName ?? "").Any(char.IsControl)) issues.Add(new("from_name_invalid", "The sender name is not valid."));
        if (Provider == Graph)
        {
            if (string.IsNullOrWhiteSpace(GraphTenantId)) issues.Add(new("authority_required", "The Entra directory (tenant) id is required."));
            if (string.IsNullOrWhiteSpace(GraphClientId)) issues.Add(new("client_id_required", "The application (client) id is required."));
            if (!Address.IsMatch(Mailbox ?? "")) issues.Add(new("mailbox_invalid", "The mailbox to send from is not valid."));
        }

        if (Provider == Smtp)
        {
            if (!ChannelRules.IsAllowedHost(SmtpHost, allowInternalHosts)) issues.Add(new("smtp_host_invalid", "The mail server address is not allowed."));
            if (SmtpPort is < 1 or > 65535) issues.Add(new("smtp_port_invalid", "The mail server port is not valid."));
            if (!string.IsNullOrEmpty(SmtpUsername) && !ChannelRules.SecretName.IsMatch(SecretRef ?? "")) issues.Add(new("secret_ref_invalid", "Give the name of the secret in the vault, not the secret itself."));
        }

        if (Provider == Graph && !ChannelRules.SecretName.IsMatch(SecretRef ?? "")) issues.Add(new("secret_ref_invalid", "Give the name of the secret in the vault, not the secret itself."));
        return issues;
    }
}

public sealed record WebhookChannelConfig(string Url, IReadOnlyList<string> Kinds)
{
    public IReadOnlyList<ConfigIssue> Validate(bool allowInternalHosts, IReadOnlyList<string> allowedHostSuffixes)
    {
        var issues = new List<ConfigIssue>();
        if (!Uri.TryCreate(Url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && !(allowInternalHosts && uri.Scheme == Uri.UriSchemeHttp)))
        {
            issues.Add(new("webhook_url_invalid", "The webhook address must be an https address."));
        }
        else if (!allowInternalHosts && (uri.UserInfo.Length > 0 || !allowedHostSuffixes.Any(s => uri.Host.EndsWith(s, StringComparison.OrdinalIgnoreCase)) || ChannelRules.IsInternalHost(uri.Host)))
        {
            issues.Add(new("webhook_host_not_allowed", "That webhook host is not on the list of allowed hosts."));
        }

        if (Kinds.Count == 0 || Kinds.Any(k => !NotificationKindsList.Contains(k))) issues.Add(new("kinds_invalid", "Choose which notifications go to the webhook."));
        return issues;
    }

    private static readonly IReadOnlyList<string> NotificationKindsList =
        ["ApprovalAssigned", "ApprovalReminder", "ApprovalEscalated", "ExceptionAssigned", "BankChangePending", "PaymentAwaitingApproval", "PaymentReleased", "ErpDeadLetter", "ReconciliationDifferences"];
}

public sealed record PushChannelConfig(string Provider)
{
    public IReadOnlyList<ConfigIssue> Validate() => Provider == "Logging" ? [] : [new ConfigIssue("provider_unknown", "Unknown push provider.")];
}

public static class ChannelRules
{
    public static readonly Regex SecretName = new("^[A-Za-z0-9][A-Za-z0-9._-]{0,99}$", RegexOptions.Compiled);
    private static readonly Regex HostName = new(@"^(?=.{1,253}$)([A-Za-z0-9]([A-Za-z0-9-]{0,61}[A-Za-z0-9])?\.)+[A-Za-z]{2,63}$", RegexOptions.Compiled);

    /// <summary>A mail server must be a public-looking name or address: not localhost, and not a private, loopback or link-local address.</summary>
    public static bool IsAllowedHost(string? host, bool allowInternal)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;
        if (allowInternal) return true;
        if (IPAddress.TryParse(host, out var ip)) return !IsInternal(ip);
        return HostName.IsMatch(host) && !IsInternalHost(host);
    }

    public static bool IsInternalHost(string host)
    {
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase)) return true;
        return IPAddress.TryParse(host, out var ip) && IsInternal(ip);
    }

    public static bool IsInternal(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip)) return true;
        if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6UniqueLocal) return true;
        if (ip.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = ip.GetAddressBytes();
        return b[0] == 10 || b[0] == 127 || b[0] == 0 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254) || (b[0] == 100 && b[1] is >= 64 and <= 127);
    }

    public static string ToJson(object config) => JsonSerializer.Serialize(config, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    public static T Parse<T>(string json) => JsonSerializer.Deserialize<T>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
}
