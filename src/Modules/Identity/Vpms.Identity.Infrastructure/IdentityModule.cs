using Microsoft.Extensions.DependencyInjection;
using Vpms.BuildingBlocks.Persistence;
using Vpms.Identity.Application;

namespace Vpms.Identity.Infrastructure;

public static class IdentityModule
{
    public static IServiceCollection AddIdentityModule(this IServiceCollection services, Func<IServiceProvider, string> connectionString)
    {
        services.AddTenantDbContext<IdentityDbContext>(connectionString);
        services.AddScoped<IIdentityStore, IdentityStore>();
        services.AddScoped<UserProvisioner>();
        services.AddSingleton(TimeProvider.System);
        return services;
    }
}
