using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vpms.BuildingBlocks.Tenancy;

namespace Vpms.BuildingBlocks.Persistence;

public static class PersistenceServiceCollectionExtensions
{
    /// <summary>Registers the scoped tenant context shared by every tenant-aware service of a request or job.</summary>
    public static IServiceCollection AddTenancy(this IServiceCollection services)
    {
        services.AddScoped<TenantContext>();
        services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());
        services.AddScoped<TenantSessionContextInterceptor>();
        services.AddScoped<Vpms.BuildingBlocks.Audit.AuditBuffer>();
        services.AddScoped<Vpms.BuildingBlocks.Audit.AuditContext>();
        services.AddScoped<Vpms.BuildingBlocks.Security.ICurrentUser, Vpms.BuildingBlocks.Security.CurrentUser>();
        services.AddScoped<Vpms.BuildingBlocks.Audit.AuditSaveChangesInterceptor>();
        return services;
    }

    public static IServiceCollection AddTenantDbContext<T>(this IServiceCollection services, string connectionString)
        where T : TenantDbContext =>
        services.AddTenantDbContext<T>(_ => connectionString);

    /// <summary>Connection string is resolved lazily so host configuration overrides (tests, Key Vault) apply.</summary>
    public static IServiceCollection AddTenantDbContext<T>(this IServiceCollection services,
        Func<IServiceProvider, string> connectionString) where T : TenantDbContext
    {
        services.AddDbContext<T>((sp, options) => options
            .UseSqlServer(connectionString(sp))
            .AddInterceptors(
                sp.GetRequiredService<TenantSessionContextInterceptor>(),
                sp.GetRequiredService<Vpms.BuildingBlocks.Audit.AuditSaveChangesInterceptor>()));
        return services;
    }
}
