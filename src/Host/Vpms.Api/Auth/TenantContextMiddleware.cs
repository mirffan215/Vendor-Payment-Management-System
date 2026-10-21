using Vpms.BuildingBlocks.Tenancy;

namespace Vpms.Api.Auth;

/// <summary>Takes the tenant only from the server-issued session claim. Headers and query strings are never trusted.</summary>
public sealed class TenantContextMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, TenantContext tenant)
    {
        if (context.User.Identity?.IsAuthenticated == true
            && Guid.TryParse(context.User.FindFirst(VpmsClaims.TenantId)?.Value, out var tenantId)
            && tenantId != Guid.Empty)
        {
            tenant.SetTenant(tenantId);
        }

        await next(context);
    }
}
