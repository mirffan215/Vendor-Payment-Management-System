using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Vpms.Audit.Application;
using Vpms.BuildingBlocks.Audit;
using Vpms.BuildingBlocks.Persistence;

namespace Vpms.Audit.Infrastructure;

public static class AuditModule
{
    public static IServiceCollection AddAuditModule(this IServiceCollection services,
        Func<IServiceProvider, string> connectionString)
    {
        // The audit context itself must not trigger audit appends, so it only gets the tenant session interceptor.
        services.AddDbContext<AuditDbContext>((sp, o) => o
            .UseSqlServer(connectionString(sp))
            .AddInterceptors(sp.GetRequiredService<TenantSessionContextInterceptor>()));
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<IAuditAppender, SqlAuditAppender>();
        services.AddScoped<IAuditPort, AuditPort>();
        services.AddScoped<IAuditQueryService, AuditQueryService>();
        services.AddScoped<IAuditChainVerifier, AuditChainVerifier>();
        services.AddScoped<IAuditEvidenceService, AuditEvidenceService>();
        services.AddScoped<IAuditAnchorService, AuditAnchorService>();
        return services;
    }
}
