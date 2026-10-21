using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Formatting.Compact;
using Vpms.CustomerAdmin.Infrastructure;

namespace Vpms.Api.Infrastructure;

public static class ObservabilityExtensions
{
    public static void AddVpmsLogging(this IHostBuilder host) =>
        host.UseSerilog((context, services, logger) => logger
            .ReadFrom.Configuration(context.Configuration)
            .ReadFrom.Services(services)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("Service", "vpms-api")
            .WriteTo.Console(new RenderedCompactJsonFormatter()));

    public static IServiceCollection AddVpmsTelemetry(this IServiceCollection services, IConfiguration configuration)
    {
        var otel = services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService("vpms-api"))
            .WithTracing(t => t
                .AddAspNetCoreInstrumentation(o => o.Filter = http => !http.Request.Path.StartsWithSegments("/health"))
                .AddHttpClientInstrumentation())
            .WithMetrics(m => m
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation());

        // Export only when a collector is configured (OTEL_EXPORTER_OTLP_ENDPOINT or Otlp:Endpoint).
        if (!string.IsNullOrEmpty(configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
        {
            otel.UseOtlpExporter();
        }

        return services;
    }

    public static IServiceCollection AddVpmsHealthChecks(this IServiceCollection services)
    {
        services.AddHealthChecks()
            .AddDbContextCheck<CustomerAdminDbContext>("sql", tags: ["ready"]);
        return services;
    }

    public static void MapVpmsHealth(this WebApplication app)
    {
        // Liveness: the process is up. Readiness: dependencies answer. Neither reveals details to anonymous callers.
        app.MapHealthChecks("/health/live", new() { Predicate = _ => false }).AllowAnonymous();
        app.MapHealthChecks("/health/ready", new() { Predicate = r => r.Tags.Contains("ready") }).AllowAnonymous();
    }
}
