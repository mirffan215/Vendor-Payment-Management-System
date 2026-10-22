using Microsoft.Extensions.DependencyInjection;
using Vpms.BuildingBlocks.Persistence;
using Vpms.Payments.Contracts;

namespace Vpms.Payments.Infrastructure;

public static class PaymentsModule
{
    public static IServiceCollection AddPaymentsModule(this IServiceCollection services, Func<IServiceProvider, string> connectionString)
    {
        services.AddTenantDbContext<PaymentsDbContext>(connectionString);
        services.AddScoped<PaymentInvalidation>();
        services.AddScoped<Vpms.Payments.Application.IPaymentRequestService, PaymentRequestService>();
        services.AddScoped<PaymentRechecker>();
        services.AddScoped<PaymentProposalService>();
        services.AddScoped<Vpms.Payments.Application.IPaymentProposalService>(sp => sp.GetRequiredService<PaymentProposalService>());
        services.AddScoped<Vpms.Payments.Application.IPaymentScan>(sp => sp.GetRequiredService<PaymentProposalService>());
        services.AddSingleton<Vpms.Payments.Application.IInstructionExporter, CsvJsonInstructionExporter>();
        services.AddScoped<Vpms.Payments.Application.ITreasuryService, TreasuryService>();
        services.AddScoped<IPaymentLifecycle, PaymentLifecycleService>();
        services.AddScoped<IPaymentReportSource, PaymentReportSource>();
        return services;
    }
}
