using System.Text.RegularExpressions;
using Vpms.Api.Auth;
using Vpms.BuildingBlocks.Observability;

namespace Vpms.Api.Infrastructure;

/// <summary>
/// Records refusals so they can be counted, alerted on and investigated: access denied, rate limited, a missing anti-forgery token, a high-risk
/// action without a recent sign-in. The log line names the route pattern and the person, never the request body, query or ids.
/// </summary>
public sealed partial class SecurityEventMiddleware(RequestDelegate next, ILogger<SecurityEventMiddleware> logger)
{
    /// <summary>Set by whoever refused the request for a reason the status code alone does not say.</summary>
    public const string ItemKey = "vpms.security_event";

    public async Task InvokeAsync(HttpContext http)
    {
        await next(http);
        var type = http.Items[ItemKey] as string ?? http.Response.StatusCode switch
        {
            StatusCodes.Status403Forbidden => "access_denied",
            StatusCodes.Status429TooManyRequests => "rate_limited",
            StatusCodes.Status401Unauthorized => "unauthenticated",
            _ => null,
        };
        if (type is null) return;

        VpmsTelemetry.SecurityEvent(type);
        // An unauthenticated call is routine (the app asks who is signed in on load); it is counted but not logged.
        if (type == "unauthenticated") return;
        var route = (http.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? Ids().Replace(http.Request.Path.Value ?? "", "{id}");
        logger.LogWarning("Security event {SecurityEvent} on {Method} {Route} for user {UserId} ({Status})",
            type, http.Request.Method, route, http.User.FindFirst(VpmsClaims.UserId)?.Value ?? "anonymous", http.Response.StatusCode);
    }

    [GeneratedRegex(@"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}|[0-9a-fA-F]{32}")]
    private static partial Regex Ids();
}
