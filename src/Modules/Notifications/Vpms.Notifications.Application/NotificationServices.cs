namespace Vpms.Notifications.Application;

public sealed record NotificationDto(Guid Id, string Kind, string Title, string Body, string Path, DateTimeOffset CreatedAt, DateTimeOffset? ReadAt, bool Mandatory);

public sealed record PreferenceDto(string Kind, string Channel, bool Enabled, bool Mandatory);

public sealed record TemplateDto(string Kind, string Channel, string Locale, string Subject, string Body, bool IsOverride);

public sealed record TemplatePreview(string Subject, string Body);

public sealed record SweepResult(int Events, int Created, IReadOnlyList<string> FailedSources);

/// <summary>What the signed-in person sees and controls.</summary>
public interface INotificationService
{
    Task<IReadOnlyList<NotificationDto>> ListAsync(bool unreadOnly, int skip, int take, string locale, CancellationToken ct = default);

    Task<int> UnreadCountAsync(CancellationToken ct = default);

    Task MarkReadAsync(Guid id, CancellationToken ct = default);

    Task<int> MarkAllReadAsync(CancellationToken ct = default);

    Task<IReadOnlyList<PreferenceDto>> PreferencesAsync(CancellationToken ct = default);

    /// <exception cref="Vpms.BuildingBlocks.Errors.ConflictException">The kind is mandatory and cannot be switched off.</exception>
    Task<PreferenceDto> SetPreferenceAsync(string kind, string channel, bool enabled, CancellationToken ct = default);
}

public interface INotificationAdmin
{
    Task<IReadOnlyList<TemplateDto>> TemplatesAsync(CancellationToken ct = default);

    Task<TemplateDto> UpsertTemplateAsync(string kind, string channel, string locale, string subject, string body, CancellationToken ct = default);

    /// <summary>Removes the tenant's wording so the built-in text applies again.</summary>
    Task ResetTemplateAsync(string kind, string channel, string locale, CancellationToken ct = default);

    TemplatePreview Preview(string kind, string locale, string subject, string body);
}

/// <summary>Turns events from the modules into notifications for the current tenant. Safe to repeat.</summary>
public interface INotificationSweep
{
    Task<SweepResult> RunAsync(CancellationToken ct = default);
}
