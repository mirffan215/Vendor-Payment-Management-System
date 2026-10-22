using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Vpms.Notifications.Contracts;
using Vpms.Notifications.Domain;

namespace Vpms.Notifications.Infrastructure;

/// <summary>Finds the wording for a message (the tenant's own, else the built-in) and fills it in.</summary>
public sealed class NotificationOptions
{
    /// <summary>The public address of the web app, put in front of a link path in emails and chat messages.</summary>
    public string BaseUrl { get; set; } = "";
}

public sealed class TemplateResolver(NotificationsDbContext db, IOptions<NotificationOptions> options)
{
    public string Absolute(string path) => options.Value.BaseUrl.TrimEnd('/') + path;

    public async Task<(string Subject, string Body)> ResolveAsync(string kind, string channel, string locale, CancellationToken ct = default)
    {
        var own = await db.Templates.AsNoTracking().FirstOrDefaultAsync(t => t.Kind == kind && t.Channel == channel && t.Locale == locale, ct);
        return own is null ? BuiltInTemplates.Get(kind, locale) : (own.Subject, own.Body);
    }

    public async Task<(string Title, string Body)> RenderAsync(Notification n, string channel, string locale, CancellationToken ct = default)
    {
        var (subject, body) = await ResolveAsync(n.Kind, channel, locale, ct);
        var values = Values(n, channel);
        var escape = channel == NotificationChannel.Email;
        var text = TemplateEngine.Render(body, values, escape);
        if (channel == NotificationChannel.InApp) text = text.TrimEnd(' ', ':', '،');
        return (TemplateEngine.Render(subject, values, false), text);
    }

    private IReadOnlyDictionary<string, string> Values(Notification n, string channel)
    {
        var data = JsonSerializer.Deserialize<Dictionary<string, string>>(n.DataJson) ?? new Dictionary<string, string>();
        data["subject"] = n.SubjectLabel;
        data["url"] = channel == NotificationChannel.InApp ? "" : Absolute(n.Path);
        return data;
    }
}
