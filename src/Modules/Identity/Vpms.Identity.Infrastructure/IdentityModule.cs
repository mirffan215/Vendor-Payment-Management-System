using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
        services.AddScoped<IApproverDirectory, ApproverDirectory>();
        services.AddScoped<IRoleAssignmentService, RoleAssignmentService>();
        services.AddSingleton(SodConfiguration.Default());
        services.AddScoped<IRoleAdminService, RoleAdminService>();
        services.AddScoped<UserAdminService>();
        services.AddScoped<IUserAdminService>(sp => sp.GetRequiredService<UserAdminService>());
        services.AddScoped<IUserDirectory>(sp => sp.GetRequiredService<UserAdminService>());
        services.AddScoped<IProviderAdminService, ProviderAdminService>();
        services.TryAddSingleton<IProviderChangeNotifier, NoOpProviderChangeNotifier>();
        services.AddSingleton(TimeProvider.System);
        return services;
    }
}
