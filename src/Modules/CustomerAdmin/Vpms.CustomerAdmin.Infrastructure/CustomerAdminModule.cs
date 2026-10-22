using Microsoft.Extensions.DependencyInjection;
using Vpms.BuildingBlocks.Persistence;
using Vpms.BuildingBlocks.Security.Encryption;
using Vpms.CustomerAdmin.Application;
using Vpms.CustomerAdmin.Contracts;

namespace Vpms.CustomerAdmin.Infrastructure;

public static class CustomerAdminModule
{
    public static IServiceCollection AddCustomerAdminModule(this IServiceCollection services, Func<IServiceProvider, string> connectionString)
    {
        services.AddTenantDbContext<CustomerAdminDbContext>(connectionString);
        services.AddScoped<ITenantLookup, TenantLookup>();
        services.AddScoped<ITenantKeyStore, TenantKeyStore>();
        services.AddScoped<ITenantProfileService, TenantProfileService>();
        services.AddScoped<LegalEntityService>();
        services.AddScoped<ILegalEntityService>(sp => sp.GetRequiredService<LegalEntityService>());
        services.AddScoped<ILegalEntityLookup>(sp => sp.GetRequiredService<LegalEntityService>());
        services.AddScoped<IApprovalAdminService, ApprovalAdminService>();
        services.AddScoped<IApprovalDataLookup, ApprovalDataLookup>();
        return services;
    }
}
