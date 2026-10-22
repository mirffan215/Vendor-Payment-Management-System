using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Vpms.BuildingBlocks.Security;
using Vpms.BuildingBlocks.Persistence;
using Vpms.ErpIntegration.Application;
using Vpms.ErpIntegration.Contracts;

namespace Vpms.ErpIntegration.Infrastructure;

public static class ErpModule
{
    public static IServiceCollection AddErpIntegrationModule(this IServiceCollection services, Func<IServiceProvider, string> connectionString)
    {
        services.AddTenantDbContext<ErpDbContext>(connectionString);
        services.TryAddSingleton<ISecretReader, ConfigurationSecretReader>();
        services.AddScoped<ErpContextResolver>();
        services.AddScoped<IErpConfigService, ErpConfigService>();
        return services;
    }
}
