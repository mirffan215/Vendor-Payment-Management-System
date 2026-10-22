using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Vpms.BuildingBlocks.Audit;
using Vpms.BuildingBlocks.Errors;
using Vpms.BuildingBlocks.Security;
using Vpms.Notifications.Application;
using Vpms.Notifications.Domain;

namespace Vpms.Notifications.Infrastructure;

public sealed class ChannelConfigService(NotificationsDbContext db, IOptions<NotificationOptions> options, IAuditPort audit, ICurrentUser current, TimeProvider clock) : INotificationChannelService
{
    private static readonly string[] OutsideChannels = [NotificationChannel.Email, NotificationChannel.Webhook, NotificationChannel.Push];

    public async Task<ChannelStateDto> GetAsync(string channel, CancellationToken ct = default)
    {
        Check(channel);
        var active = await db.ChannelConfigs.AsNoTracking().FirstOrDefaultAsync(c => c.Channel == channel && c.Status == ChannelConfigStatus.Active, ct);
        var pending = await db.ChannelConfigs.AsNoTracking().FirstOrDefaultAsync(c => c.Channel == channel && c.Status == ChannelConfigStatus.Pending, ct);
        return new ChannelStateDto(active is null ? null : Map(active), pending is null ? null : Map(pending));
    }

    public async Task<ChannelConfigDto> ProposeAsync(string channel, JsonElement config, string note, CancellationToken ct = default)
    {
        Check(channel);
        if (string.IsNullOrWhiteSpace(note) || note.Length > 500) throw new ValidationFailedException("reason_required", "Say why the channel changes (max 500 characters).");
        var json = Normalise(channel, config);

        if (await db.ChannelConfigs.AnyAsync(c => c.Channel == channel && c.Status == ChannelConfigStatus.Pending, ct)) throw new ConflictException("change_pending", "There is already a change waiting for approval.");
        var active = await db.ChannelConfigs.AsNoTracking().FirstOrDefaultAsync(c => c.Channel == channel && c.Status == ChannelConfigStatus.Active, ct);
        if (active is not null && active.ConfigJson == json) throw new ConflictException("no_change", "This is the configuration already in force.");

        var version = (await db.ChannelConfigs.Where(c => c.Channel == channel).MaxAsync(c => (int?)c.VersionNo, ct) ?? 0) + 1;
        var row = new ChannelConfig { Id = Guid.NewGuid(), Channel = channel, VersionNo = version, ConfigJson = json, Note = note.Trim(), CreatedBy = current.UserId, CreatedAt = clock.GetUtcNow() };
        try
        {
            db.ChannelConfigs.Add(row);
            audit.Record(new AuditEntry("notification.channel.proposed", "ChannelConfig", row.Id.ToString(), null, JsonSerializer.Serialize(new { channel, version })));
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            throw new ConflictException("concurrent_change", "The channel was changed at the same time. Reload and try again.");
        }

        return Map(row);
    }

    public async Task<IReadOnlyList<ChannelConfigDto>> ListPendingAsync(CancellationToken ct = default) =>
        (await db.ChannelConfigs.AsNoTracking().Where(c => c.Status == ChannelConfigStatus.Pending).OrderBy(c => c.CreatedAt).ToListAsync(ct)).Select(Map).ToList();

    public async Task<ChannelConfigDto> ApproveAsync(Guid changeId, string? comment, CancellationToken ct = default)
    {
        var row = await Load(changeId, ct);
        try
        {
            row.Approve(current.UserId, comment, clock.GetUtcNow());
        }
        catch (SegregationOfDutiesException ex)
        {
            await audit.WriteNowAsync(new AuditEntry("sod.notification-channel.blocked", "ChannelConfig", changeId.ToString(), null, JsonSerializer.Serialize(new { attempted = "approve", rule = ex.Rule })), ct);
            throw;
        }

        var active = await db.ChannelConfigs.FirstOrDefaultAsync(c => c.Channel == row.Channel && c.Status == ChannelConfigStatus.Active && c.Id != row.Id, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        row.Status = ChannelConfigStatus.Pending;
        if (active is not null)
        {
            active.Supersede();
            await db.SaveChangesAsync(ct);
        }

        row.Status = ChannelConfigStatus.Active;
        audit.Record(new AuditEntry("notification.channel.approved", "ChannelConfig", changeId.ToString(), null, JsonSerializer.Serialize(new { row.Channel, version = row.VersionNo })));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Map(row);
    }

    public async Task<ChannelConfigDto> RejectAsync(Guid changeId, string comment, CancellationToken ct = default)
    {
        var row = await Load(changeId, ct);
        try
        {
            row.Reject(current.UserId, comment, clock.GetUtcNow());
        }
        catch (SegregationOfDutiesException ex)
        {
            await audit.WriteNowAsync(new AuditEntry("sod.notification-channel.blocked", "ChannelConfig", changeId.ToString(), null, JsonSerializer.Serialize(new { attempted = "reject", rule = ex.Rule })), ct);
            throw;
        }

        audit.Record(new AuditEntry("notification.channel.rejected", "ChannelConfig", changeId.ToString(), null));
        await db.SaveChangesAsync(ct);
        return Map(row);
    }

    public async Task<ChannelConfigDto> CancelAsync(Guid changeId, CancellationToken ct = default)
    {
        var row = await Load(changeId, ct);
        row.Cancel(current.UserId, clock.GetUtcNow());
        audit.Record(new AuditEntry("notification.channel.cancelled", "ChannelConfig", changeId.ToString(), null));
        await db.SaveChangesAsync(ct);
        return Map(row);
    }

    public async Task<DeliverySummaryDto> SummaryAsync(CancellationToken ct = default)
    {
        var counts = await db.Deliveries.AsNoTracking().GroupBy(d => d.Status).Select(g => new { Status = g.Key, Count = g.Count() }).ToListAsync(ct);
        int Of(params string[] s) => counts.Where(c => s.Contains(c.Status)).Sum(c => c.Count);
        return new DeliverySummaryDto(Of(DeliveryStatus.Pending, DeliveryStatus.InFlight), Of(DeliveryStatus.Sent), Of(DeliveryStatus.Skipped), Of(DeliveryStatus.DeadLetter));
    }

    public async Task<IReadOnlyList<DeliveryDto>> DeadLettersAsync(int take, CancellationToken ct = default) =>
        await (from d in db.Deliveries.AsNoTracking()
               join n in db.Notifications.AsNoTracking() on d.NotificationId equals n.Id
               where d.Status == DeliveryStatus.DeadLetter
               orderby d.CreatedAt descending
               select new DeliveryDto(d.Id, d.Channel, d.Status, d.Attempts, d.LastError, d.CreatedAt, d.SentAt, n.Kind, n.SubjectLabel)).Take(Math.Clamp(take, 1, 200)).ToListAsync(ct);

    // --- internals -------------------------------------------------------------------------------------------

    private static void Check(string channel)
    {
        if (!OutsideChannels.Contains(channel)) throw new ValidationFailedException("unknown_channel", "That channel cannot be configured.");
    }

    /// <summary>Parses the settings for the channel, checks them, and returns the canonical JSON that is stored (secret names only).</summary>
    private string Normalise(string channel, JsonElement config)
    {
        var o = options.Value;
        IReadOnlyList<ConfigIssue> issues;
        string json;
        switch (channel)
        {
            case NotificationChannel.Email:
                var email = Parse<EmailChannelConfig>(config);
                issues = email.Validate(o.AllowInternalHosts);
                json = ChannelRules.ToJson(email);
                break;
            case NotificationChannel.Webhook:
                var hook = Parse<WebhookChannelConfig>(config);
                issues = hook.Validate(o.AllowInternalHosts, o.WebhookHostSuffixes);
                json = ChannelRules.ToJson(hook);
                break;
            default:
                var push = Parse<PushChannelConfig>(config);
                issues = push.Validate();
                json = ChannelRules.ToJson(push);
                break;
        }

        if (issues.Count > 0) throw new ValidationFailedException(issues[0].Code, issues[0].Message);
        return json;
    }

    private static T Parse<T>(JsonElement config)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(config.GetRawText(), new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? throw new ValidationFailedException("config_invalid", "The settings are not valid.");
        }
        catch (JsonException)
        {
            throw new ValidationFailedException("config_invalid", "The settings are not valid.");
        }
    }

    private async Task<ChannelConfig> Load(Guid id, CancellationToken ct) => await db.ChannelConfigs.FirstOrDefaultAsync(c => c.Id == id, ct) ?? throw new NotFoundException("Channel change");

    private static ChannelConfigDto Map(ChannelConfig c) =>
        new(c.Id, c.Channel, c.VersionNo, c.Status, JsonDocument.Parse(c.ConfigJson).RootElement.Clone(), c.Note, c.CreatedBy, c.CreatedAt, c.DecidedBy, c.DecidedAt, c.DecisionComment);
}
