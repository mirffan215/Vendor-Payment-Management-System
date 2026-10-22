using System.Text.Json;

namespace Vpms.Notifications.Application;

public sealed record ChannelConfigDto(
    Guid Id, string Channel, int VersionNo, string Status, JsonElement Config, string Note, Guid CreatedBy, DateTimeOffset CreatedAt, Guid? DecidedBy, DateTimeOffset? DecidedAt, string? DecisionComment);

public sealed record ChannelStateDto(ChannelConfigDto? Active, ChannelConfigDto? Pending);

public sealed record DeliverySummaryDto(int Pending, int Sent, int Skipped, int DeadLetter);

public sealed record DeliveryDto(Guid Id, string Channel, string Status, int Attempts, string? LastError, DateTimeOffset CreatedAt, DateTimeOffset? SentAt, string Kind, string SubjectLabel);

public sealed record DeliveryRunResult(int Processed, int Sent, int Retrying, int DeadLettered, int Skipped);

public interface INotificationChannelService
{
    Task<ChannelStateDto> GetAsync(string channel, CancellationToken ct = default);

    /// <param name="config">The channel's settings as JSON; checked against the channel's rules before anything is stored.</param>
    Task<ChannelConfigDto> ProposeAsync(string channel, JsonElement config, string note, CancellationToken ct = default);

    Task<IReadOnlyList<ChannelConfigDto>> ListPendingAsync(CancellationToken ct = default);

    Task<ChannelConfigDto> ApproveAsync(Guid changeId, string? comment, CancellationToken ct = default);

    Task<ChannelConfigDto> RejectAsync(Guid changeId, string comment, CancellationToken ct = default);

    Task<ChannelConfigDto> CancelAsync(Guid changeId, CancellationToken ct = default);

    Task<DeliverySummaryDto> SummaryAsync(CancellationToken ct = default);

    Task<IReadOnlyList<DeliveryDto>> DeadLettersAsync(int take, CancellationToken ct = default);
}

/// <summary>Sends what is due on the outside channels for the current tenant.</summary>
public interface INotificationDelivery
{
    Task<DeliveryRunResult> ProcessDueAsync(int batch, CancellationToken ct = default);
}

public enum SendOutcome
{
    Sent,
    Transient,
    Permanent,
}

public sealed record SendResult(SendOutcome Outcome, string? Code = null, TimeSpan? RetryAfter = null)
{
    public static SendResult Ok() => new(SendOutcome.Sent);

    public static SendResult Retry(string code, TimeSpan? after = null) => new(SendOutcome.Transient, code, after);

    public static SendResult Fail(string code) => new(SendOutcome.Permanent, code);
}

public sealed record OutgoingEmail(string To, string Subject, string HtmlBody, string TextBody, string FromAddress, string? FromName);

public interface IEmailSender
{
    /// <summary>"Graph" or "Smtp": which email configuration this sender serves.</summary>
    string Provider { get; }

    Task<SendResult> SendAsync(string settingsJson, string? secret, OutgoingEmail email, CancellationToken ct = default);
}

public sealed record WebhookMessage(string Title, string Body, string? Url);

public interface IWebhookSender
{
    Task<SendResult> SendAsync(string url, WebhookMessage message, CancellationToken ct = default);
}

/// <summary>Mobile push is only an abstraction in this version: nothing registers devices yet, so the only provider writes to the log.</summary>
public interface IPushSender
{
    string Provider { get; }

    Task<SendResult> SendAsync(Guid userId, string title, string body, string? path, CancellationToken ct = default);
}
