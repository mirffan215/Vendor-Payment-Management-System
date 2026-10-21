using Serilog;
using Serilog.Context;
using Vpms.Api.Auth;
using Vpms.BuildingBlocks.Audit;
using Vpms.BuildingBlocks.Tenancy;

namespace Vpms.Api.Infrastructure;

/// <summary>Puts tenant, user and correlation ids on every log line and trace of the request.</summary>
public sealed class LogEnrichmentMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext http, ITenantContext tenant, AuditContext audit, IDiagnosticContext diagnostics)
    {
        var tenantId = tenant.TenantId?.ToString();
        var userId = http.User.FindFirst(VpmsClaims.UserId)?.Value;

        // The request-completion log is written outside this scope, so it gets the same properties via the diagnostic context.
        diagnostics.Set("TenantId", tenantId);
        diagnostics.Set("UserId", userId);
        diagnostics.Set("CorrelationId", audit.CorrelationId);

        using (LogContext.PushProperty("TenantId", tenantId))
        using (LogContext.PushProperty("UserId", userId))
        using (LogContext.PushProperty("CorrelationId", audit.CorrelationId))
        {
            var activity = System.Diagnostics.Activity.Current;
            activity?.SetTag("vpms.tenant_id", tenantId);
            activity?.SetTag("vpms.correlation_id", audit.CorrelationId);
            await next(http);
        }
    }
}
