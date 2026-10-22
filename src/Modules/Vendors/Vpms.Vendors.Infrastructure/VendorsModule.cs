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
        services.AddScoped<IVendorErpSource, VendorErpSource>();
        services.AddScoped<Vpms.Notifications.Contracts.INotificationSource, VendorNotificationSource>();
        services.AddScoped<VendorDocumentService>();
        services.AddScoped<IVendorDocumentService>(sp => sp.GetRequiredService<VendorDocumentService>());
        services.AddScoped<IVendorPortalService, VendorPortalService>();
        services.AddScoped<IVendorEntityLinkService, VendorEntityLinkService>();
        services.AddScoped<IVendorRequestService, VendorRequestService>();
        services.AddScoped<BankDetailsService>();
        services.AddScoped<IBankDetailsService>(sp => sp.GetRequiredService<BankDetailsService>());
        services.AddScoped<IBankDetailsLookup>(sp => sp.GetRequiredService<BankDetailsService>());
        services.AddScoped<IBankChangeExpiryScan>(sp => sp.GetRequiredService<BankDetailsService>());
        return services;
    }
}
