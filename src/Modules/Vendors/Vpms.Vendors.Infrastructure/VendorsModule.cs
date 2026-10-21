using Microsoft.Extensions.DependencyInjection;
using Vpms.BuildingBlocks.Persistence;
using Vpms.Vendors.Application;
using Vpms.Vendors.Contracts;

namespace Vpms.Vendors.Infrastructure;

public static class VendorsModule
{
    public static IServiceCollection AddVendorsModule(this IServiceCollection services, Func<IServiceProvider, string> connectionString)
    {
        services.AddTenantDbContext<VendorsDbContext>(connectionString);
        services.AddScoped<VendorService>();
        services.AddScoped<IVendorService>(sp => sp.GetRequiredService<VendorService>());
        services.AddScoped<IVendorLookup>(sp => sp.GetRequiredService<VendorService>());
        services.AddScoped<IVendorDocumentService, VendorDocumentService>();
        services.AddScoped<IVendorEntityLinkService, VendorEntityLinkService>();
        services.AddScoped<IVendorRequestService, VendorRequestService>();
        return services;
    }
}
