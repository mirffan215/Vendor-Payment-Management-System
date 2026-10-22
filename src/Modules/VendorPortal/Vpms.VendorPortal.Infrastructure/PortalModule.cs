using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Vpms.BuildingBlocks.Persistence;
using Vpms.VendorPortal.Application;
using Vpms.VendorPortal.Domain;

namespace Vpms.VendorPortal.Infrastructure;

public static class PortalModule
{
    public static IServiceCollection AddVendorPortalModule(this IServiceCollection services, Func<IServiceProvider, string> connectionString)
    {
        services.AddOptions<PortalOptions>();
        services.AddTenantDbContext<PortalDbContext>(connectionString);
        services.AddDataProtection();
        services.AddSingleton<IPasswordHasher<VendorUser>>(sp =>
            new PasswordHasher<VendorUser>(Options.Create(new PasswordHasherOptions
            {
                CompatibilityMode = PasswordHasherCompatibilityMode.IdentityV3,
                IterationCount = sp.GetRequiredService<IOptions<PortalOptions>>().Value.PasswordHashIterations,
            })));
        services.AddSingleton<DummyPasswordHash>();
        services.AddScoped<IPortalAdminService, PortalAdminService>();
        services.AddScoped<IPortalAuthService, PortalAuthService>();
        services.TryAddSingleton<IInviteDelivery, LoggingInviteDelivery>();
        return services;
    }
}

/// <summary>Records that an invitation was issued. It never logs the link: the link is a credential.</summary>
public sealed class LoggingInviteDelivery(ILogger<LoggingInviteDelivery> logger) : IInviteDelivery
{
    public Task DeliverAsync(string email, string displayName, string link, DateTimeOffset expiresAt, CancellationToken ct = default)
    {
        logger.LogInformation("Portal invitation issued (expires {Expires}); pass the link to the recipient through a secure channel.", expiresAt);
        return Task.CompletedTask;
    }
}
