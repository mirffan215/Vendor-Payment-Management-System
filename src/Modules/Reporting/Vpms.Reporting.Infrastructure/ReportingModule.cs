using Microsoft.Extensions.DependencyInjection;
using Vpms.Reporting.Application;

namespace Vpms.Reporting.Infrastructure;

public static class ReportingModule
{
    public static IServiceCollection AddReportingModule(this IServiceCollection services)
    {
        services.AddSingleton<IReportExporter, CsvReportExporter>();
        services.AddScoped<IReportService, ReportService>();
        return services;
    }
}
