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
        services.Configure<D365.D365Options>(o => {
            o.Authority = configuration[$"{D365.D365Options.Section}:Authority"] ?? o.Authority;
            o.AllowInternalHosts = string.Equals(configuration[$"{D365.D365Options.Section}:AllowInternalHosts"], "true", StringComparison.OrdinalIgnoreCase);
        });
        services.AddSingleton<IErpAdapter>(sp => new D365.D365FinanceAdapter(
            new HttpClient(Vpms.BuildingBlocks.Net.OutboundHttp.CreateHandler(sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<D365.D365Options>>().Value.AllowInternalHosts)), sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<D365.D365Options>>(),
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
        services.AddScoped<Vpms.Notifications.Contracts.INotificationSource, ErpNotificationSource>();
        services.AddScoped<ErpInbound>();
        services.AddScoped<IErpInbound>(sp => sp.GetRequiredService<ErpInbound>());
        services.AddScoped<IErpInboundQuery>(sp => sp.GetRequiredService<ErpInbound>());
        return services;
    }
}
