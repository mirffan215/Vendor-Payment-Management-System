using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Vpms.BuildingBlocks.Observability;
using Vpms.CustomerAdmin.Infrastructure;

namespace Vpms.Api.Infrastructure;

public static class ObservabilityExtensions
{
    /// <summary>Logs (scrubbed), traces and metrics through the shared setup, plus ASP.NET Core request instrumentation.</summary>
    public static IServiceCollection AddVpmsTelemetry(this IServiceCollection services, IConfiguration configuration) =>
        services.AddVpmsObservability(configuration, "vpms-api",
            tracing: t => t.AddAspNetCoreInstrumentation(o => o.Filter = http => !http.Request.Path.StartsWithSegments("/health")),
            metrics: m => m.AddAspNetCoreInstrumentation());

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
