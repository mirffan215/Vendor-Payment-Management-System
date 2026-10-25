using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Vpms.Audit.Application;
using Vpms.BuildingBlocks.Audit;
using Vpms.BuildingBlocks.Persistence;

namespace Vpms.Audit.Infrastructure;

public static class AuditModule
{
    /// <summary>The key under which the storage backend for the anchor container is registered.</summary>
    public const string AnchorBackendKey = "audit-anchors";

    /// <summary>
    /// Chooses where chain heads are published outside the database. <c>Audit:AnchorSink</c> is "Blob" in production (a container with an immutability
    /// policy, registered under <see cref="AnchorBackendKey"/>) or "File" (a directory, for development).
    /// </summary>
    public static IServiceCollection AddAuditAnchorSink(this IServiceCollection services)
    {
        services.AddSingleton<IAuditAnchorSink>(sp =>
        {
            var configuration = sp.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>();
            return string.Equals(configuration["Audit:AnchorSink"], "Blob", StringComparison.OrdinalIgnoreCase)
                ? new BlobAuditAnchorSink(sp.GetRequiredKeyedService<Vpms.BuildingBlocks.Storage.IBlobBackend>(AnchorBackendKey))
                : new FileAuditAnchorSink(configuration["Audit:AnchorDirectory"] ?? Path.Combine(Path.GetTempPath(), "vpms-audit-anchors"));
        });
        return services;
    }

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
        services.AddScoped<IAuditRestoreService, AuditRestoreService>();
        return services;
    }
}
