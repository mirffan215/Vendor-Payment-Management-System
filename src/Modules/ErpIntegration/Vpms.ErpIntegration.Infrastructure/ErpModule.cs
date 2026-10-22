using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Vpms.BuildingBlocks.Security;
using Vpms.BuildingBlocks.Persistence;
using Vpms.ErpIntegration.Application;
using Vpms.ErpIntegration.Contracts;

namespace Vpms.ErpIntegration.Infrastructure;

public static class ErpModule
{
    /// <summary>Makes Dynamics 365 Finance selectable as a provider. The mock is never registered here.</summary>
    public static IServiceCollection AddD365FinanceAdapter(this IServiceCollection services, Microsoft.Extensions.Configuration.IConfiguration configuration)
    {
        services.Configure<D365.D365Options>(o => o.Authority = configuration[$"{D365.D365Options.Section}:Authority"] ?? o.Authority);
        services.AddSingleton<IErpAdapter>(sp => new D365.D365FinanceAdapter(
            new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) }), sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<D365.D365Options>>(),
            sp.GetRequiredService<TimeProvider>()));
        return services;
    }

    public static IServiceCollection AddErpIntegrationModule(this IServiceCollection services, Func<IServiceProvider, string> connectionString)
    {
        services.AddTenantDbContext<ErpDbContext>(connectionString);
        services.TryAddSingleton<ISecretReader, ConfigurationSecretReader>();
        services.AddScoped<ErpContextResolver>();
        services.AddScoped<IErpConfigService, ErpConfigService>();
        services.AddSingleton<ErpJitter>();
        services.AddScoped<IErpOutbound, ErpOutbound>();
        services.AddScoped<ErpSyncService>();
        services.AddScoped<IErpSyncService>(sp => sp.GetRequiredService<ErpSyncService>());
        services.AddScoped<IErpScan>(sp => sp.GetRequiredService<ErpSyncService>());
        services.AddScoped<IReconciliationService, ReconciliationService>();
        services.AddScoped<ErpInbound>();
        services.AddScoped<IErpInbound>(sp => sp.GetRequiredService<ErpInbound>());
        services.AddScoped<IErpInboundQuery>(sp => sp.GetRequiredService<ErpInbound>());
        return services;
    }
}
