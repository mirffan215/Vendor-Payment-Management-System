using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Vpms.BuildingBlocks.Tenancy;
using Vpms.Identity.Contracts;
using Vpms.Notifications.Application;
using Vpms.Notifications.Contracts;
using Vpms.Notifications.Domain;

namespace Vpms.Notifications.Infrastructure;

/// <summary>
/// Asks each module what has happened since last time and tells the people it concerns. Windows overlap on purpose and the dedupe key makes
/// a repeat harmless, so a missed or interrupted run loses nothing. A source that fails is skipped (and tried again next time) without
/// holding back the others.
/// </summary>
public sealed class NotificationSweep(
    NotificationsDbContext db, IEnumerable<INotificationSource> sources, IApproverDirectory directory, ITenantContext tenant, TimeProvider clock, ILogger<NotificationSweep> logger) : INotificationSweep
{
    private static readonly TimeSpan Overlap = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan FirstLookback = TimeSpan.FromHours(24);

    public async Task<SweepResult> RunAsync(CancellationToken ct = default)
    {
        var tenantId = tenant.RequireTenantId();
        var now = clock.GetUtcNow();
        int events = 0, created = 0;
        var failed = new List<string>();
        var off = (await db.Preferences.AsNoTracking().Where(p => !p.Enabled).Select(p => new { p.UserId, p.Kind, p.Channel }).ToListAsync(ct)).Select(p => (p.UserId, p.Kind, p.Channel)).ToHashSet();
        var channels = (await db.ChannelConfigs.AsNoTracking().Where(c => c.Status == ChannelConfigStatus.Active).ToListAsync(ct)).ToDictionary(c => c.Channel);
        var webhookKinds = channels.TryGetValue(NotificationChannel.Webhook, out var hook) ? ChannelRules.Parse<WebhookChannelConfig>(hook.ConfigJson).Kinds.ToHashSet() : new HashSet<string>();
        var webhookKeys = new HashSet<string>();
        var switchedOff = (await db.Preferences.AsNoTracking().Where(p => p.Channel == NotificationChannel.InApp && !p.Enabled).Select(p => new { p.UserId, p.Kind }).ToListAsync(ct))
            .Select(p => (p.UserId, p.Kind)).ToHashSet();

        foreach (var source in sources)
        {
            var cursor = await db.Cursors.FirstOrDefaultAsync(c => c.Source == source.Name, ct);
            var since = cursor is null ? now - FirstLookback : cursor.LastPolledAt - Overlap;
            IReadOnlyList<NotificationEvent> polled;
            try
            {
                polled = await source.PollAsync(since, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Notification source {Source} failed.", source.Name);
                failed.Add(source.Name);
                continue;
            }

            events += polled.Count;
            var keys = polled.Select(e => e.DedupeKey).Distinct().ToList();
            var known = (await db.Notifications.AsNoTracking().Where(n => keys.Contains(n.DedupeKey)).Select(n => new { n.UserId, n.DedupeKey }).ToListAsync(ct)).Select(n => (n.UserId, n.DedupeKey)).ToHashSet();

            foreach (var e in polled)
            {
                foreach (var user in await AudienceAsync(e, ct))
                {
                    if (known.Contains((user, e.DedupeKey))) continue;
                    known.Add((user, e.DedupeKey));
                    if (!NotificationKinds.Mandatory.Contains(e.Kind) && switchedOff.Contains((user, e.Kind))) continue;
                    var notification = new Notification
                    {
                        Id = Guid.NewGuid(), TenantId = tenantId, UserId = user, Kind = e.Kind, LegalEntityId = e.LegalEntityId, SubjectLabel = Cut(e.SubjectLabel, 300), Path = e.Path,
                        DataJson = JsonSerializer.Serialize(e.Data), DedupeKey = e.DedupeKey, CreatedAt = now,
                    };
                    db.Notifications.Add(notification);
                    created++;
                    await PlanDeliveriesAsync(notification, channels, webhookKinds, webhookKeys, off, now, ct);
                }
            }

            if (cursor is null) db.Cursors.Add(cursor = new NotificationCursor { Id = Guid.NewGuid(), TenantId = tenantId, Source = source.Name });
            cursor.LastPolledAt = now;

            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (ex.InnerException?.Message.Contains("duplicate key", StringComparison.OrdinalIgnoreCase) == true)
            {
                // Another worker told these people first; the unique key kept it to one notification each. The next sweep carries on.
                db.ChangeTracker.Clear();
                logger.LogInformation("Another worker recorded the same notifications first for {Source}.", source.Name);
            }
        }

        return new SweepResult(events, created, failed);
    }

    /// <summary>Email and push go to the person (unless they switched that off; mandatory kinds cannot be). The webhook is a shared channel, so it gets one message per event, not one per recipient.</summary>
    private async Task PlanDeliveriesAsync(Notification n, Dictionary<string, ChannelConfig> channels, HashSet<string> webhookKinds, HashSet<string> webhookKeys, HashSet<(Guid, string, string)> off, DateTimeOffset now, CancellationToken ct)
    {
        var mandatory = NotificationKinds.Mandatory.Contains(n.Kind);
        foreach (var channel in new[] { NotificationChannel.Email, NotificationChannel.Push })
        {
            if (channels.ContainsKey(channel) && (mandatory || !off.Contains((n.UserId, n.Kind, channel)))) AddDelivery(n, channel, now);
        }

        if (channels.ContainsKey(NotificationChannel.Webhook) && webhookKinds.Contains(n.Kind) && webhookKeys.Add(n.DedupeKey)
            && !await db.Deliveries.AnyAsync(d => d.Channel == NotificationChannel.Webhook && db.Notifications.Any(x => x.Id == d.NotificationId && x.DedupeKey == n.DedupeKey), ct))
        {
            AddDelivery(n, NotificationChannel.Webhook, now);
        }
    }

    private void AddDelivery(Notification n, string channel, DateTimeOffset now) =>
        db.Deliveries.Add(new NotificationDelivery { Id = Guid.NewGuid(), TenantId = n.TenantId, NotificationId = n.Id, UserId = n.UserId, Channel = channel, NextAttemptAt = now, CreatedAt = now });

    private async Task<IReadOnlyList<Guid>> AudienceAsync(NotificationEvent e, CancellationToken ct)
    {
        if (e.Users.Count > 0) return e.Users.Distinct().ToList();
        if (e.Permission is not null && e.LegalEntityId is { } entity) return (await directory.ListPermissionHoldersAsync(entity, e.Permission, ct)).Distinct().ToList();
        return [];
    }

    private static string Cut(string text, int max) => text.Length <= max ? text : text[..max];
}
