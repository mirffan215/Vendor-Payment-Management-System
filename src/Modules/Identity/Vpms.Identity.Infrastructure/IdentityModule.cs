using Microsoft.Extensions.DependencyInjection;
using Vpms.BuildingBlocks.Persistence;
using Vpms.Identity.Application;
using Vpms.Identity.Contracts;

namespace Vpms.Identity.Infrastructure;

public static class IdentityModule
{
    public static IServiceCollection AddIdentityModule(this IServiceCollection services, Func<IServiceProvider, string> connectionString)
    {
        services.AddTenantDbContext<IdentityDbContext>(connectionString);
        services.AddScoped<IIdentityStore, IdentityStore>();
        services.AddScoped<UserProvisioner>();
        services.AddScoped<IPermissionEvaluator, PermissionEvaluator>();
        services.AddScoped<IRoleAssignmentService, RoleAssignmentService>();
        services.AddSingleton(SodConfiguration.Default());
        services.AddSingleton(TimeProvider.System);
        return services;
    }
}
