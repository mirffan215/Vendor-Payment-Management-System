using Microsoft.Extensions.DependencyInjection;
using Vpms.BuildingBlocks.Persistence;
using Vpms.CustomerAdmin.Contracts;

namespace Vpms.CustomerAdmin.Infrastructure;

public static class CustomerAdminModule
{
    public static IServiceCollection AddCustomerAdminModule(this IServiceCollection services, Func<IServiceProvider, string> connectionString)
    {
        services.AddTenantDbContext<CustomerAdminDbContext>(connectionString);
        services.AddScoped<ITenantLookup, TenantLookup>();
        return services;
    }
}
