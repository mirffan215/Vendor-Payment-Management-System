using Microsoft.AspNetCore.Antiforgery;

namespace Vpms.Api.Auth;

/// <summary>Cookie sessions require a valid antiforgery header on every state-changing request.</summary>
public sealed class CsrfProtectionMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, IAntiforgery antiforgery)
    {
        var path = context.Request.Path;
        var unsafeMethod = !HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method)
                           && !HttpMethods.IsOptions(context.Request.Method) && !HttpMethods.IsTrace(context.Request.Method);
        var protectedPath = path.StartsWithSegments("/api") || path.StartsWithSegments("/auth/logout");

        if (unsafeMethod && protectedPath && context.User.Identity?.IsAuthenticated == true)
        {
            try
            {
                await antiforgery.ValidateRequestAsync(context);
            }
            catch (AntiforgeryValidationException)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsJsonAsync(new Microsoft.AspNetCore.Mvc.ProblemDetails
                {
                    Status = 400,
                    Title = "Invalid or missing antiforgery token.",
                    Type = "https://vpms.dev/problems/csrf",
                });
                return;
            }
        }

        await next(context);
    }
}
