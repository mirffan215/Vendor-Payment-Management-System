using System.Diagnostics;
using Vpms.BuildingBlocks.Audit;

namespace Vpms.Api.Auth;

/// <summary>
/// Runs BEFORE authentication so sign-in events (handled by the OIDC callback) also carry a correlation id and
/// client address. The actor is added by <see cref="AuditActorMiddleware"/> once the user is known.
/// </summary>
public sealed class AuditContextMiddleware(RequestDelegate next)
{
    public const string CorrelationHeader = "X-Correlation-Id";

    public async Task InvokeAsync(HttpContext http, AuditContext audit)
    {
        var supplied = http.Request.Headers[CorrelationHeader].ToString();
        audit.CorrelationId = IsSafe(supplied) ? supplied : Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N");
        http.Response.Headers[CorrelationHeader] = audit.CorrelationId;
        audit.IpAddress = http.Connection.RemoteIpAddress?.ToString();

        await next(http);
    }

    private static bool IsSafe(string value) =>
        value.Length is > 0 and <= 100 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
}

/// <summary>Sets the acting user on the audit context after authentication.</summary>
public sealed class AuditActorMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext http, AuditContext audit)
    {
        if (Guid.TryParse(http.User.FindFirst(VpmsClaims.UserId)?.Value, out var userId))
        {
            audit.ActorId = userId;
            audit.ActorType = AuditActorType.User;
        }

        await next(http);
    }
}
