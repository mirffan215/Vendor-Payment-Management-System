using Microsoft.Extensions.DependencyInjection;
using Vpms.BuildingBlocks.Persistence;
using Vpms.Invoices.Application;

namespace Vpms.Invoices.Infrastructure;

public static class InvoicesModule
{
    public static IServiceCollection AddInvoicesModule(this IServiceCollection services, Func<IServiceProvider, string> connectionString)
    {
        services.AddTenantDbContext<InvoicesDbContext>(connectionString);
        services.AddScoped<InvoiceService>();
        services.AddScoped<IInvoiceService>(sp => sp.GetRequiredService<InvoiceService>());
        services.AddScoped<InvoiceAttachmentService>();
        services.AddScoped<IInvoiceAttachmentService>(sp => sp.GetRequiredService<InvoiceAttachmentService>());
        return services;
    }
}
