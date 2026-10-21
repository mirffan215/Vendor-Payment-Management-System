using Microsoft.Extensions.DependencyInjection;
using Vpms.BuildingBlocks.Persistence;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
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
        services.AddScoped<InvoiceExtractionService>();
        services.AddScoped<IInvoiceExtractionService>(sp => sp.GetRequiredService<InvoiceExtractionService>());
        services.TryAddSingleton<IDocumentExtractor, NoOpDocumentExtractor>();
        services.AddScoped<InvoiceIntakeService>();
        services.AddScoped<IInvoiceIntakeService>(sp => sp.GetRequiredService<InvoiceIntakeService>());
        services.AddScoped<IIntakeScan>(sp => sp.GetRequiredService<InvoiceIntakeService>());
        services.AddScoped<IPortalInvoiceService, PortalInvoiceService>();
        services.AddScoped<IVerificationRuleSetService, VerificationRuleSetService>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<Vpms.Invoices.Domain.Tax.ITaxRuleProvider, Vpms.Invoices.Domain.Tax.StandardVatProvider>());
        services.AddScoped<InvoiceVerifier>();
        services.AddScoped<IInvoiceVerifier>(sp => sp.GetRequiredService<InvoiceVerifier>());
        services.AddScoped<IVerificationScan>(sp => sp.GetRequiredService<InvoiceVerifier>());
        services.AddScoped<IInvoiceExceptionService, InvoiceExceptionService>();
        services.AddScoped<IPurchaseOrderService, PurchaseOrderService>();
        // Microsoft Graph by default (does nothing until it is configured); tests and local development replace IMailboxReader.
        services.TryAddSingleton<IMailboxReader>(sp => new GraphMailboxReader(
            new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) }),
            sp.GetRequiredService<IOptions<GraphOptions>>(), sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<GraphMailboxReader>>()));
        services.AddOptions<GraphOptions>();
        return services;
    }
}
