using Microsoft.Extensions.DependencyInjection;
using Vpms.Reporting.Application;

namespace Vpms.Reporting.Infrastructure;

public static class ReportingModule
{
    public static IServiceCollection AddReportingModule(this IServiceCollection services)
    {
        services.AddSingleton<IReportExporter, CsvReportExporter>();
        services.AddSingleton<IReportExporter, XlsxReportExporter>();
        services.AddSingleton<IReportExporter, PdfReportExporter>();
        services.AddScoped<IReportService, ReportService>();
        return services;
    }
}
