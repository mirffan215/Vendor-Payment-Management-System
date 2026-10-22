using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;

namespace Vpms.Api.Auth;

/// <summary>
/// For actions where a stolen or left-open session must not be enough: revealing a bank account, approving a bank-detail change, releasing a
/// payment, changing who may do what, or where the organisation signs in. The person must have signed in (at their identity provider, not just
/// held a cookie) within <see cref="AuthOptions.StepUpWindow"/>. Otherwise the answer is 403 "step_up_required" and the app sends them through
/// <c>/auth/step-up</c>, which asks the identity provider to authenticate them again.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public sealed class RequireStepUpAttribute : Attribute, IAsyncAuthorizationFilter
{
    public const string ErrorCode = "step_up_required";

    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        var http = context.HttpContext;
        var options = http.RequestServices.GetRequiredService<IOptions<AuthOptions>>().Value;
        var clock = http.RequestServices.GetRequiredService<TimeProvider>();
        if (long.TryParse(http.User.FindFirst(VpmsClaims.AuthTime)?.Value, CultureInfo.InvariantCulture, out var authTime)
            && clock.GetUtcNow() - DateTimeOffset.FromUnixTimeSeconds(authTime) <= options.StepUpWindow)
        {
            return;
        }

        http.Items[Infrastructure.SecurityEventMiddleware.ItemKey] = "step_up_required";
        http.Response.StatusCode = StatusCodes.Status403Forbidden;
        await http.RequestServices.GetRequiredService<IProblemDetailsService>().WriteAsync(new ProblemDetailsContext
        {
            HttpContext = http,
            ProblemDetails = new ProblemDetails
            {
                Status = StatusCodes.Status403Forbidden,
                Title = "Please confirm it is you before doing this.",
                Type = Infrastructure.ProblemDetailsExceptionHandler.TypeBase + ErrorCode,
                Extensions = { ["errorCode"] = ErrorCode, ["stepUpUrl"] = "/auth/step-up" },
            },
        });
        context.Result = new EmptyResult();
    }
}
