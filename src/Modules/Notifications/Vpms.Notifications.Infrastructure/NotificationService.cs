using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vpms.BuildingBlocks.Audit;
using Vpms.BuildingBlocks.Errors;
using Vpms.BuildingBlocks.Security;
using Vpms.Notifications.Application;
using Vpms.Notifications.Contracts;
using Vpms.Notifications.Domain;

namespace Vpms.Notifications.Infrastructure;

public sealed class NotificationService(NotificationsDbContext db, TemplateResolver templates, ICurrentUser current, TimeProvider clock) : INotificationService
{
    public async Task<IReadOnlyList<NotificationDto>> ListAsync(bool unreadOnly, int skip, int take, string locale, CancellationToken ct = default)
    {
        var user = current.UserId;
        var q = db.Notifications.AsNoTracking().Where(n => n.UserId == user);
        if (unreadOnly) q = q.Where(n => n.ReadAt == null);
        var rows = await q.OrderByDescending(n => n.CreatedAt).Skip(Math.Max(0, skip)).Take(Math.Clamp(take, 1, 100)).ToListAsync(ct);
        var result = new List<NotificationDto>();
        foreach (var n in rows)
        {
            var (title, body) = await templates.RenderAsync(n, NotificationChannel.InApp, locale == "ar" ? "ar" : "en", ct);
            result.Add(new NotificationDto(n.Id, n.Kind, title, body, n.Path, n.CreatedAt, n.ReadAt, NotificationKinds.Mandatory.Contains(n.Kind)));
        }

        return result;
    }

    public Task<int> UnreadCountAsync(CancellationToken ct = default)
    {
        var user = current.UserId;
        return db.Notifications.CountAsync(n => n.UserId == user && n.ReadAt == null, ct);
    }

    public async Task MarkReadAsync(Guid id, CancellationToken ct = default)
    {
        var user = current.UserId;
        var n = await db.Notifications.FirstOrDefaultAsync(x => x.Id == id && x.UserId == user, ct) ?? throw new NotFoundException("Notification");
        if (n.ReadAt is null)
        {
            n.ReadAt = clock.GetUtcNow();
            await db.SaveChangesAsync(ct);
        }
    }

    public async Task<int> MarkAllReadAsync(CancellationToken ct = default)
    {
        var user = current.UserId;
        var now = clock.GetUtcNow();
        var unread = await db.Notifications.Where(n => n.UserId == user && n.ReadAt == null).ToListAsync(ct);
        foreach (var n in unread) n.ReadAt = now;
        await db.SaveChangesAsync(ct);
        return unread.Count;
    }

    public async Task<IReadOnlyList<PreferenceDto>> PreferencesAsync(CancellationToken ct = default)
    {
        var user = current.UserId;
        var rows = await db.Preferences.AsNoTracking().Where(p => p.UserId == user).ToListAsync(ct);
        return NotificationKinds.All.SelectMany(kind => NotificationChannel.All.Select(channel =>
        {
            var mandatory = NotificationKinds.Mandatory.Contains(kind);
            var row = rows.FirstOrDefault(r => r.Kind == kind && r.Channel == channel);
            return new PreferenceDto(kind, channel, mandatory || (row?.Enabled ?? true), mandatory);
        })).ToList();
    }

    public async Task<PreferenceDto> SetPreferenceAsync(string kind, string channel, bool enabled, CancellationToken ct = default)
    {
        if (!NotificationKinds.All.Contains(kind) || !NotificationChannel.All.Contains(channel)) throw new ValidationFailedException("unknown_preference", "That notification or channel does not exist.");
        var mandatory = NotificationKinds.Mandatory.Contains(kind);
        if (mandatory && !enabled) throw new ConflictException("notification_mandatory", "This notification cannot be switched off.");

        var user = current.UserId;
        var row = await db.Preferences.FirstOrDefaultAsync(p => p.UserId == user && p.Kind == kind && p.Channel == channel, ct);
        if (row is null) db.Preferences.Add(row = new NotificationPreference { Id = Guid.NewGuid(), UserId = user, Kind = kind, Channel = channel });
        row.Enabled = enabled;
        await db.SaveChangesAsync(ct);
        return new PreferenceDto(kind, channel, enabled, mandatory);
    }
}

public sealed class NotificationAdminService(NotificationsDbContext db, IAuditPort audit, ICurrentUser current, TimeProvider clock) : INotificationAdmin
{
    public async Task<IReadOnlyList<TemplateDto>> TemplatesAsync(CancellationToken ct = default)
    {
        var own = await db.Templates.AsNoTracking().ToListAsync(ct);
        var list = new List<TemplateDto>();
        foreach (var kind in NotificationKinds.All)
        {
            foreach (var channel in new[] { NotificationChannel.InApp, NotificationChannel.Email, NotificationChannel.Webhook })
            {
                foreach (var locale in new[] { "en", "ar" })
                {
                    var row = own.FirstOrDefault(t => t.Kind == kind && t.Channel == channel && t.Locale == locale);
                    var (subject, body) = row is null ? BuiltInTemplates.Get(kind, locale) : (row.Subject, row.Body);
                    list.Add(new TemplateDto(kind, channel, locale, subject, body, row is not null));
                }
            }
        }

        return list;
    }

    public async Task<TemplateDto> UpsertTemplateAsync(string kind, string channel, string locale, string subject, string body, CancellationToken ct = default)
    {
        Check(kind, channel, locale);
        var problems = TemplateEngine.Validate(subject, body);
        if (problems.Count > 0) throw new ValidationFailedException(problems[0].Split(':')[0], "The template is not valid: " + string.Join(", ", problems));

        var row = await db.Templates.FirstOrDefaultAsync(t => t.Kind == kind && t.Channel == channel && t.Locale == locale, ct);
        if (row is null) db.Templates.Add(row = new NotificationTemplate { Id = Guid.NewGuid(), Kind = kind, Channel = channel, Locale = locale });
        row.Subject = subject.Trim();
        row.Body = body.Trim();
        row.UpdatedBy = current.UserId;
        row.UpdatedAt = clock.GetUtcNow();
        audit.Record(new AuditEntry("notification.template.changed", "NotificationTemplate", $"{kind}:{channel}:{locale}", null, JsonSerializer.Serialize(new { kind, channel, locale })));
        await db.SaveChangesAsync(ct);
        return new TemplateDto(kind, channel, locale, row.Subject, row.Body, true);
    }

    public async Task ResetTemplateAsync(string kind, string channel, string locale, CancellationToken ct = default)
    {
        Check(kind, channel, locale);
        var row = await db.Templates.FirstOrDefaultAsync(t => t.Kind == kind && t.Channel == channel && t.Locale == locale, ct);
        if (row is null) return;
        db.Templates.Remove(row);
        audit.Record(new AuditEntry("notification.template.reset", "NotificationTemplate", $"{kind}:{channel}:{locale}", null, JsonSerializer.Serialize(new { kind, channel, locale })));
        await db.SaveChangesAsync(ct);
    }

    public TemplatePreview Preview(string kind, string locale, string subject, string body)
    {
        var problems = TemplateEngine.Validate(subject, body);
        if (problems.Count > 0) throw new ValidationFailedException(problems[0].Split(':')[0], "The template is not valid: " + string.Join(", ", problems));
        var sample = new Dictionary<string, string> { ["subject"] = locale == "ar" ? "INV-1001 - شركة الأمل" : "INV-1001 - Al Amal Trading", ["detail"] = "level 1", ["count"] = "3", ["url"] = "https://app.example.com/approvals/123" };
        return new TemplatePreview(TemplateEngine.Render(subject, sample, false), TemplateEngine.Render(body, sample, false));
    }

    private static void Check(string kind, string channel, string locale)
    {
        if (!NotificationKinds.All.Contains(kind) || !new[] { NotificationChannel.InApp, NotificationChannel.Email, NotificationChannel.Webhook }.Contains(channel) || locale is not ("en" or "ar"))
        {
            throw new ValidationFailedException("unknown_template", "That template does not exist.");
        }
    }
}
