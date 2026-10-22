using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Vpms.BuildingBlocks.Security;
using Vpms.BuildingBlocks.Tenancy;
using Vpms.CustomerAdmin.Contracts;
using Vpms.Identity.Contracts;
using Vpms.Notifications.Application;
using Vpms.Notifications.Domain;

namespace Vpms.Notifications.Infrastructure;

/// <summary>
/// Sends the deliveries that are due. A person with no usable email address is skipped, not failed. Throttling and outages retry with a
/// growing delay and end in the dead letter; a refusal that retrying cannot fix goes there at once.
/// </summary>
public sealed class NotificationDeliveryService(
    NotificationsDbContext db, TemplateResolver templates, IUserDirectory users, ITenantLookup tenants, ITenantContext tenant, ISecretReader secrets,
    IEnumerable<IEmailSender> emailSenders, IWebhookSender webhook, IEnumerable<IPushSender> pushSenders, TimeProvider clock, ILogger<NotificationDeliveryService> logger) : INotificationDelivery
{
    public async Task<DeliveryRunResult> ProcessDueAsync(int batch, CancellationToken ct = default)
    {
        int processed = 0, sent = 0, retrying = 0, dead = 0, skipped = 0;
        var now = clock.GetUtcNow();
        var due = await db.Deliveries.Where(d => (d.Status == DeliveryStatus.Pending && d.NextAttemptAt <= now) || (d.Status == DeliveryStatus.InFlight && d.LeaseUntil <= now))
            .OrderBy(d => d.NextAttemptAt).Take(Math.Clamp(batch, 1, 200)).ToListAsync(ct);
        var locale = (await tenants.GetDefaultsAsync(tenant.RequireTenantId(), ct))?.Locale ?? "en";

        foreach (var delivery in due)
        {
            try
            {
                delivery.Lease(clock.GetUtcNow());
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                db.Entry(delivery).State = EntityState.Detached; // another worker has it
                continue;
            }

            processed++;
            SendResult? result;
            try
            {
                result = await SendAsync(delivery, locale, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unexpected failure sending notification delivery {DeliveryId}.", delivery.Id);
                result = SendResult.Retry("unexpected_error");
            }

            var after = clock.GetUtcNow();
            switch (result?.Outcome)
            {
                case null: skipped++; break; // skipped inside SendAsync
                case SendOutcome.Sent: delivery.MarkSent(after); sent++; break;
                case SendOutcome.Permanent: delivery.FailPermanent(result.Code ?? "failed"); dead++; break;
                default:
                    delivery.FailTransient(result.Code ?? "failed", after, result.RetryAfter);
                    if (delivery.Status == DeliveryStatus.DeadLetter) dead++; else retrying++;
                    break;
            }

            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                logger.LogWarning("Lost the lease on notification delivery {DeliveryId} while sending.", delivery.Id);
                db.ChangeTracker.Clear();
            }
        }

        return new DeliveryRunResult(processed, sent, retrying, dead, skipped);
    }

    /// <summary>Returns null when the delivery was skipped (the delivery is already marked).</summary>
    private async Task<SendResult?> SendAsync(NotificationDelivery d, string locale, CancellationToken ct)
    {
        var n = await db.Notifications.AsNoTracking().FirstOrDefaultAsync(x => x.Id == d.NotificationId, ct);
        if (n is null) { d.Skip("notification_missing"); return null; }
        var config = await db.ChannelConfigs.AsNoTracking().FirstOrDefaultAsync(c => c.Channel == d.Channel && c.Status == ChannelConfigStatus.Active, ct);
        if (config is null) { d.Skip("channel_not_configured"); return null; }

        switch (d.Channel)
        {
            case NotificationChannel.Email:
            {
                var person = (await users.GetAsync([d.UserId], ct)).GetValueOrDefault(d.UserId);
                if (person is null || person.Status != "Active" || string.IsNullOrWhiteSpace(person.Email)) { d.Skip("no_email_address"); return null; }
                var settings = ChannelRules.Parse<EmailChannelConfig>(config.ConfigJson);
                var sender = emailSenders.LastOrDefault(s => s.Provider == settings.Provider);
                if (sender is null) return SendResult.Fail("provider_unavailable");
                var secret = string.IsNullOrEmpty(settings.SecretRef) ? null : await secrets.ReadAsync(settings.SecretRef, ct);

                var (title, html) = await templates.RenderAsync(n, NotificationChannel.Email, locale, ct);
                var text = (await templates.RenderAsync(n, NotificationChannel.InApp, locale, ct)).Body + "\n" + templates.Absolute(n.Path);
                var wrapped = $"<div dir=\"{(locale == "ar" ? "rtl" : "ltr")}\" style=\"font-family:sans-serif\"><p>{html}</p></div>";
                return await sender.SendAsync(config.ConfigJson, secret, new OutgoingEmail(person.Email, title, wrapped, text, settings.FromAddress, settings.FromName), ct);
            }

            case NotificationChannel.Webhook:
            {
                var settings = ChannelRules.Parse<WebhookChannelConfig>(config.ConfigJson);
                var (title, body) = await templates.RenderAsync(n, NotificationChannel.Webhook, locale, ct);
                return await webhook.SendAsync(settings.Url, new WebhookMessage(title, body, templates.Absolute(n.Path)), ct);
            }

            case NotificationChannel.Push:
            {
                var settings = ChannelRules.Parse<PushChannelConfig>(config.ConfigJson);
                var sender = pushSenders.LastOrDefault(s => s.Provider == settings.Provider);
                if (sender is null) return SendResult.Fail("provider_unavailable");
                var (title, body) = await templates.RenderAsync(n, NotificationChannel.InApp, locale, ct);
                return await sender.SendAsync(d.UserId, title, body, n.Path, ct);
            }

            default:
                d.Skip("unknown_channel");
                return null;
        }
    }
}
