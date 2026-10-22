using Microsoft.Extensions.DependencyInjection;
using Vpms.BuildingBlocks.Persistence;
using Vpms.Notifications.Application;

namespace Vpms.Notifications.Infrastructure;

public static class NotificationsModule
{
    public static IServiceCollection AddNotificationsModule(this IServiceCollection services, Func<IServiceProvider, string> connectionString, Action<NotificationOptions>? configure = null)
    {
        services.AddTenantDbContext<NotificationsDbContext>(connectionString);
        services.Configure<NotificationOptions>(o => configure?.Invoke(o));
        services.AddScoped<TemplateResolver>();
        services.AddScoped<INotificationService, NotificationService>();
        services.AddScoped<Vpms.BuildingBlocks.Jobs.IRetentionTarget, NotificationRetentionTarget>();
        services.AddScoped<INotificationAdmin, NotificationAdminService>();
        services.AddScoped<INotificationSweep, NotificationSweep>();
        services.AddScoped<INotificationChannelService, ChannelConfigService>();
        services.AddScoped<INotificationDelivery, NotificationDeliveryService>();
        services.AddSingleton<IEmailSender, SmtpEmailSender>();
        services.AddSingleton<IPushSender, LoggingPushSender>();
        // One shared HttpClient each (connections are pooled and recycled), like the other outbound clients.
        services.AddSingleton<IEmailSender>(sp => new GraphEmailSender(
            new HttpClient(Vpms.BuildingBlocks.Net.OutboundHttp.CreateHandler(sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<NotificationOptions>>().Value.AllowInternalHosts)) { Timeout = TimeSpan.FromSeconds(30) },
            sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<GraphMailOptions>>(), sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<IWebhookSender>(sp => new TeamsWebhookSender(
            new HttpClient(Vpms.BuildingBlocks.Net.OutboundHttp.CreateHandler(sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<NotificationOptions>>().Value.AllowInternalHosts)) { Timeout = TimeSpan.FromSeconds(30) },
            sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<NotificationOptions>>()));
        services.AddOptions<GraphMailOptions>();
        return services;
    }
}
