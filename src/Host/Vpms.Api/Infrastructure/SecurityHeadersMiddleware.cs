namespace Vpms.Api.Infrastructure;

/// <summary>Defensive headers for an API: no sniffing, no framing, no caching of authenticated responses.</summary>
public sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext http)
    {
        http.Response.OnStarting(() =>
        {
            var h = http.Response.Headers;
            h["X-Content-Type-Options"] = "nosniff";
            h["X-Frame-Options"] = "DENY";
            h["Referrer-Policy"] = "no-referrer";
            h["Cross-Origin-Resource-Policy"] = "same-origin";
            h["Permissions-Policy"] = "geolocation=(), camera=(), microphone=()";
            if (!h.ContainsKey("Content-Security-Policy"))
            {
                h["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'none'";
            }

            if (http.Request.Path.StartsWithSegments("/api") || http.Request.Path.StartsWithSegments("/auth"))
            {
                h["Cache-Control"] = "no-store";
            }

            return Task.CompletedTask;
        });
        return next(http);
    }
}
