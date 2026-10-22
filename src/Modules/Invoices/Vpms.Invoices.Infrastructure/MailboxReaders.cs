using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Vpms.Invoices.Application;

namespace Vpms.Invoices.Infrastructure;

public sealed class GraphOptions
{
    public const string Section = "Graph";

    /// <summary>Entra tenant of the app registration that has Mail.ReadWrite on the invoice mailboxes (application permission).</summary>
    public string? TenantId { get; set; }

    public string? ClientId { get; set; }

    /// <summary>Supplied from Key Vault in deployed environments; never stored in the repository.</summary>
    public string? ClientSecret { get; set; }

    public string BaseUrl { get; set; } = "https://graph.microsoft.com/v1.0";
    public string AuthorityUrl { get; set; } = "https://login.microsoftonline.com";
    public long MaxAttachmentBytes { get; set; } = 25 * 1024 * 1024;

    public bool Configured => !string.IsNullOrWhiteSpace(TenantId) && !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);
}

/// <summary>
/// Reads invoice mailboxes through Microsoft Graph with application permissions (client credentials). Unread messages that have
/// attachments are returned oldest first; handled messages are marked read. Only file attachments are taken.
/// </summary>
public sealed class GraphMailboxReader(HttpClient http, IOptions<GraphOptions> options, TimeProvider clock, ILogger<GraphMailboxReader> logger) : IMailboxReader
{
    private string? _token;
    private DateTimeOffset _tokenExpires;

    public async Task<IReadOnlyList<InboundMail>> FetchUnprocessedAsync(string mailbox, int max, CancellationToken ct = default)
    {
        var o = options.Value;
        if (!o.Configured)
        {
            logger.LogWarning("Microsoft Graph is not configured; the invoice mailbox of {Mailbox} was not read.", mailbox);
            return [];
        }

        var user = Uri.EscapeDataString(mailbox);
        var url = $"{o.BaseUrl.TrimEnd('/')}/users/{user}/mailFolders/inbox/messages?$filter=isRead eq false and hasAttachments eq true" +
                  $"&$orderby=receivedDateTime asc&$top={Math.Clamp(max, 1, 50)}&$select=id,subject,from,receivedDateTime";
        var list = await GetAsync<GraphList<GraphMessage>>(url, ct);

        var result = new List<InboundMail>();
        foreach (var m in list.Value)
        {
            var attachmentsUrl = $"{o.BaseUrl.TrimEnd('/')}/users/{user}/messages/{Uri.EscapeDataString(m.Id)}/attachments?$select=name,contentType,size,contentBytes";
            var attachments = await GetAsync<GraphList<GraphAttachment>>(attachmentsUrl, ct);
            var files = attachments.Value
                .Where(a => a.ODataType == "#microsoft.graph.fileAttachment" && a.ContentBytes is not null && a.Size <= o.MaxAttachmentBytes)
                .Select(a => new InboundAttachment(a.Name ?? "attachment", a.ContentType ?? "application/octet-stream", Convert.FromBase64String(a.ContentBytes!)))
                .ToList();
            result.Add(new InboundMail(m.Id, m.From?.EmailAddress?.Address ?? "", m.Subject ?? "", m.ReceivedDateTime, files));
        }

        return result;
    }

    public async Task MarkProcessedAsync(string mailbox, string messageId, CancellationToken ct = default)
    {
        var o = options.Value;
        if (!o.Configured) return;
        using var request = new HttpRequestMessage(HttpMethod.Patch, $"{o.BaseUrl.TrimEnd('/')}/users/{Uri.EscapeDataString(mailbox)}/messages/{Uri.EscapeDataString(messageId)}")
        {
            Content = JsonContent.Create(new { isRead = true }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await TokenAsync(ct));
        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
    }

    private async Task<T> GetAsync<T>(string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await TokenAsync(ct));
        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(ct) ?? throw new InvalidOperationException("Empty response from Microsoft Graph.");
    }

    private async Task<string> TokenAsync(CancellationToken ct)
    {
        if (_token is not null && clock.GetUtcNow() < _tokenExpires) return _token;
        var o = options.Value;
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{o.AuthorityUrl.TrimEnd('/')}/{Uri.EscapeDataString(o.TenantId!)}/oauth2/v2.0/token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials", ["client_id"] = o.ClientId!, ["client_secret"] = o.ClientSecret!,
                ["scope"] = "https://graph.microsoft.com/.default",
            }),
        };
        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var token = await response.Content.ReadFromJsonAsync<GraphToken>(ct) ?? throw new InvalidOperationException("Empty token response.");
        _token = token.AccessToken;
        _tokenExpires = clock.GetUtcNow().AddSeconds(Math.Max(60, token.ExpiresIn - 60));
        return _token;
    }

    private sealed record GraphToken([property: JsonPropertyName("access_token")] string AccessToken, [property: JsonPropertyName("expires_in")] int ExpiresIn);

    private sealed record GraphList<T>([property: JsonPropertyName("value")] List<T> Value);

    private sealed record GraphMessage(
        [property: JsonPropertyName("id")] string Id, [property: JsonPropertyName("subject")] string? Subject,
        [property: JsonPropertyName("from")] GraphFrom? From, [property: JsonPropertyName("receivedDateTime")] DateTimeOffset ReceivedDateTime);

    private sealed record GraphFrom([property: JsonPropertyName("emailAddress")] GraphEmail? EmailAddress);

    private sealed record GraphEmail([property: JsonPropertyName("address")] string? Address);

    private sealed record GraphAttachment(
        [property: JsonPropertyName("@odata.type")] string? ODataType, [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("contentType")] string? ContentType, [property: JsonPropertyName("size")] long Size,
        [property: JsonPropertyName("contentBytes")] string? ContentBytes);
}

/// <summary>A mailbox held in memory, for tests and local development.</summary>
public sealed class InMemoryMailboxReader : IMailboxReader
{
    private readonly object _gate = new();
    private readonly List<(string Mailbox, InboundMail Message, bool Processed)> _messages = [];

    public bool FailReads { get; set; }

    public void Add(string mailbox, InboundMail message)
    {
        lock (_gate) _messages.Add((mailbox, message, false));
    }

    public int ProcessedCount(string mailbox)
    {
        lock (_gate) return _messages.Count(m => m.Mailbox == mailbox && m.Processed);
    }

    public Task<IReadOnlyList<InboundMail>> FetchUnprocessedAsync(string mailbox, int max, CancellationToken ct = default)
    {
        if (FailReads) throw new InvalidOperationException("The mailbox is unavailable.");
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<InboundMail>>(_messages.Where(m => m.Mailbox == mailbox && !m.Processed)
                .OrderBy(m => m.Message.ReceivedAt).Take(max).Select(m => m.Message).ToList());
        }
    }

    public Task MarkProcessedAsync(string mailbox, string messageId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var i = _messages.FindIndex(m => m.Mailbox == mailbox && m.Message.Id == messageId);
            if (i >= 0) _messages[i] = (mailbox, _messages[i].Message, true);
        }

        return Task.CompletedTask;
    }
}
