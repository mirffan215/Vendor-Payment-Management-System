using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;
using Vpms.Notifications.Application;
using Vpms.Notifications.Domain;

namespace Vpms.Notifications.Infrastructure;

public sealed class GraphMailOptions
{
    public string BaseUrl { get; set; } = "https://graph.microsoft.com/v1.0";

    public string AuthorityUrl { get; set; } = "https://login.microsoftonline.com";
}

internal static class Http
{
    public static SendResult Classify(HttpStatusCode status, HttpResponseMessage response, string what)
    {
        var code = (int)status;
        var retryAfter = response.Headers.RetryAfter?.Delta;
        if (code == 429) return SendResult.Retry($"{what}_throttled", retryAfter ?? TimeSpan.FromMinutes(1));
        if (code is 408 or >= 500) return SendResult.Retry($"{what}_http_{code}", retryAfter);
        if (code is 401 or 403) return SendResult.Retry($"{what}_auth_failed"); // a rotated secret recovers without a manual retry
        return SendResult.Fail($"{what}_http_{code}");
    }
}

/// <summary>Sends mail as a mailbox through Microsoft Graph (application permission Mail.Send, client credentials). Not tested against live Graph.</summary>
public sealed class GraphEmailSender(HttpClient http, IOptions<GraphMailOptions> options, TimeProvider clock) : IEmailSender
{
    private readonly object _gate = new();
    private readonly Dictionary<string, (string Token, DateTimeOffset Expires)> _tokens = new();

    public string Provider => EmailChannelConfig.Graph;

    public async Task<SendResult> SendAsync(string settingsJson, string? secret, OutgoingEmail email, CancellationToken ct = default)
    {
        var settings = ChannelRules.Parse<EmailChannelConfig>(settingsJson);
        if (string.IsNullOrEmpty(secret)) return SendResult.Retry("secret_unavailable");

        var token = await TokenAsync(settings, secret, ct);
        if (token.Result is not null) return token.Result;

        var body = new JsonObject
        {
            ["message"] = new JsonObject
            {
                ["subject"] = email.Subject,
                ["body"] = new JsonObject { ["contentType"] = "HTML", ["content"] = email.HtmlBody },
                ["toRecipients"] = new JsonArray(new JsonObject { ["emailAddress"] = new JsonObject { ["address"] = email.To } }),
            },
            ["saveToSentItems"] = false,
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{options.Value.BaseUrl.TrimEnd('/')}/users/{Uri.EscapeDataString(settings.Mailbox!)}/sendMail")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Value);
        try
        {
            using var response = await http.SendAsync(request, ct);
            return response.IsSuccessStatusCode ? SendResult.Ok() : Http.Classify(response.StatusCode, response, "graph");
        }
        catch (HttpRequestException)
        {
            return SendResult.Retry("graph_network_error");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return SendResult.Retry("graph_timeout");
        }
    }

    private async Task<(string? Value, SendResult? Result)> TokenAsync(EmailChannelConfig s, string secret, CancellationToken ct)
    {
        var key = $"{s.GraphTenantId}|{s.GraphClientId}|{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret)))[..12]}";
        lock (_gate)
        {
            if (_tokens.TryGetValue(key, out var cached) && cached.Expires > clock.GetUtcNow().AddMinutes(2)) return (cached.Token, null);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{options.Value.AuthorityUrl.TrimEnd('/')}/{Uri.EscapeDataString(s.GraphTenantId!)}/oauth2/v2.0/token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials", ["client_id"] = s.GraphClientId!, ["client_secret"] = secret, ["scope"] = "https://graph.microsoft.com/.default",
            }),
        };
        try
        {
            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return (null, (int)response.StatusCode is 429 or >= 500 ? SendResult.Retry("graph_token_unavailable") : SendResult.Retry("graph_auth_failed"));
            var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
            var token = json?["access_token"]?.GetValue<string>();
            if (string.IsNullOrEmpty(token)) return (null, SendResult.Retry("graph_auth_failed"));
            lock (_gate) _tokens[key] = (token, clock.GetUtcNow().AddSeconds(json?["expires_in"]?.GetValue<int>() ?? 3000));
            return (token, null);
        }
        catch (HttpRequestException)
        {
            return (null, SendResult.Retry("graph_network_error"));
        }
    }
}

/// <summary>Sends mail through an SMTP server (STARTTLS when asked). The password, if any, is the secret passed in; it is never logged.</summary>
public sealed class SmtpEmailSender(ILogger<SmtpEmailSender> logger) : IEmailSender
{
    public string Provider => EmailChannelConfig.Smtp;

    public async Task<SendResult> SendAsync(string settingsJson, string? secret, OutgoingEmail email, CancellationToken ct = default)
    {
        var s = ChannelRules.Parse<EmailChannelConfig>(settingsJson);
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(email.FromName ?? "", email.FromAddress));
        message.To.Add(MailboxAddress.Parse(email.To));
        message.Subject = email.Subject;
        message.Body = new BodyBuilder { HtmlBody = email.HtmlBody, TextBody = email.TextBody }.ToMessageBody();

        try
        {
            using var client = new SmtpClient { Timeout = 30_000 };
            await client.ConnectAsync(s.SmtpHost!, s.SmtpPort, s.SmtpStartTls ? SecureSocketOptions.StartTls : SecureSocketOptions.None, ct);
            if (!string.IsNullOrEmpty(s.SmtpUsername))
            {
                if (string.IsNullOrEmpty(secret)) return SendResult.Retry("secret_unavailable");
                await client.AuthenticateAsync(s.SmtpUsername, secret, ct);
            }

            await client.SendAsync(message, ct);
            await client.DisconnectAsync(true, ct);
            return SendResult.Ok();
        }
        catch (AuthenticationException)
        {
            return SendResult.Retry("smtp_auth_failed");
        }
        catch (SmtpCommandException ex) when ((int)ex.StatusCode >= 500)
        {
            return SendResult.Fail($"smtp_{(int)ex.StatusCode}");
        }
        catch (Exception ex) when (ex is SmtpCommandException or SmtpProtocolException or IOException or System.Net.Sockets.SocketException or TimeoutException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogWarning("SMTP send failed: {Type}", ex.GetType().Name);
            return SendResult.Retry("smtp_unavailable");
        }
    }
}

/// <summary>Posts an adaptive card to a Teams (Workflows / incoming webhook) address. The address is checked against the allow-list again at send time.</summary>
public sealed class TeamsWebhookSender(HttpClient http, IOptions<NotificationOptions> options) : IWebhookSender
{
    public async Task<SendResult> SendAsync(string url, WebhookMessage message, CancellationToken ct = default)
    {
        var check = new WebhookChannelConfig(url, ["ErpDeadLetter"]).Validate(options.Value.AllowInternalHosts, options.Value.WebhookHostSuffixes);
        if (check.Any(i => i.Code is "webhook_url_invalid" or "webhook_host_not_allowed")) return SendResult.Fail("webhook_not_allowed");

        var card = new JsonObject
        {
            ["type"] = "message",
            ["attachments"] = new JsonArray(new JsonObject
            {
                ["contentType"] = "application/vnd.microsoft.card.adaptive",
                ["content"] = new JsonObject
                {
                    ["$schema"] = "http://adaptivecards.io/schemas/adaptive-card.json",
                    ["type"] = "AdaptiveCard",
                    ["version"] = "1.4",
                    ["body"] = new JsonArray(
                        new JsonObject { ["type"] = "TextBlock", ["text"] = message.Title, ["weight"] = "Bolder", ["wrap"] = true },
                        new JsonObject { ["type"] = "TextBlock", ["text"] = message.Body, ["wrap"] = true }),
                    ["actions"] = message.Url is null ? new JsonArray() : new JsonArray(new JsonObject { ["type"] = "Action.OpenUrl", ["title"] = "Open", ["url"] = message.Url }),
                },
            }),
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(card.ToJsonString(), Encoding.UTF8, "application/json") };
        try
        {
            using var response = await http.SendAsync(request, ct);
            return response.IsSuccessStatusCode ? SendResult.Ok() : Http.Classify(response.StatusCode, response, "webhook");
        }
        catch (HttpRequestException)
        {
            return SendResult.Retry("webhook_network_error");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return SendResult.Retry("webhook_timeout");
        }
    }
}

/// <summary>The only push provider: records that a push would have gone out. Real push needs device registration, which does not exist yet.</summary>
public sealed class LoggingPushSender(ILogger<LoggingPushSender> logger) : IPushSender
{
    public string Provider => "Logging";

    public Task<SendResult> SendAsync(Guid userId, string title, string body, string? path, CancellationToken ct = default)
    {
        logger.LogInformation("Push (not delivered, no provider): user {UserId} kind path {Path}", userId, path);
        return Task.FromResult(SendResult.Ok());
    }
}
