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
        services.AddScoped<INotificationAdmin, NotificationAdminService>();
        services.AddScoped<INotificationSweep, NotificationSweep>();
        return services;
    }
}
