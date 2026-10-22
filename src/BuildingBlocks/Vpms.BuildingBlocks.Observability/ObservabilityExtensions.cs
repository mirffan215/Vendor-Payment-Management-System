using Azure.Monitor.OpenTelemetry.Exporter;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Formatting.Compact;

namespace Vpms.BuildingBlocks.Observability;

public static class ObservabilityExtensions
{
    /// <summary>
    /// Structured JSON logs to the console with secrets scrubbed, traces and metrics through OpenTelemetry. Export goes to Application Insights when
    /// APPLICATIONINSIGHTS_CONNECTION_STRING is set and to any OTLP collector when OTEL_EXPORTER_OTLP_ENDPOINT is set; with neither, signals stay local.
    /// </summary>
    public static IServiceCollection AddVpmsObservability(this IServiceCollection services, IConfiguration configuration, string serviceName,
        Action<TracerProviderBuilder>? tracing = null, Action<MeterProviderBuilder>? metrics = null)
    {
        services.AddSerilog((sp, logger) => logger
            .ReadFrom.Configuration(configuration)
            .ReadFrom.Services(sp)
            .Enrich.FromLogContext()
            .Enrich.With<RedactingEnricher>()
            .Enrich.WithProperty("Service", serviceName)
            .WriteTo.Console(new ScrubbingFormatter(new RenderedCompactJsonFormatter())));

        var otel = services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService(serviceName, serviceInstanceId: Environment.MachineName))
            .WithTracing(t =>
            {
                t.AddHttpClientInstrumentation()
                    .AddSqlClientInstrumentation(o => o.RecordException = true) // statement text is never recorded
                    .AddSource(VpmsTelemetry.Name);
                tracing?.Invoke(t);
            })
            .WithMetrics(m =>
            {
                m.AddHttpClientInstrumentation().AddRuntimeInstrumentation().AddMeter(VpmsTelemetry.Name);
                metrics?.Invoke(m);
            });

        if (!string.IsNullOrEmpty(configuration["OTEL_EXPORTER_OTLP_ENDPOINT"])) otel.UseOtlpExporter();
        if (!string.IsNullOrEmpty(configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]))
        {
            otel.UseAzureMonitorExporter(o => o.ConnectionString = configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]);
        }

        return services;
    }
}
