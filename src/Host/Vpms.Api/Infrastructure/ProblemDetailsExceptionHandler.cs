using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Vpms.BuildingBlocks.Audit;
using Vpms.BuildingBlocks.Errors;
using Vpms.BuildingBlocks.Security;
using Vpms.BuildingBlocks.Tenancy;
using Vpms.VendorPortal.Application;

namespace Vpms.Api.Infrastructure;

/// <summary>RFC 7807 responses with stable error codes. Internal details never reach the client.</summary>
public sealed class ProblemDetailsExceptionHandler(ILogger<ProblemDetailsExceptionHandler> logger, IProblemDetailsService problems)
    : IExceptionHandler
{
    public const string TypeBase = "https://vpms.dev/problems/";

    public async ValueTask<bool> TryHandleAsync(HttpContext http, Exception exception, CancellationToken ct)
    {
        var (status, code, title, detail) = Map(exception);

        if (status >= 500)
        {
            logger.LogError(exception, "Unhandled exception {ErrorCode}", code);
        }
        else
        {
            logger.LogWarning("Request rejected: {ErrorCode} ({ExceptionType})", code, exception.GetType().Name);
        }

        var problem = new ProblemDetails { Status = status, Title = title, Detail = detail, Type = TypeBase + code };
        problem.Extensions["errorCode"] = code;
        if (exception is SegregationOfDutiesException sod)
        {
            problem.Extensions["rule"] = sod.Rule;
        }

        if (code is "validation_failed" or "conflict")
        {
            problem.Extensions["errorCode"] = exception switch
            {
                ValidationFailedException v => v.Code,
                ConflictException c => c.Code,
                _ => code,
            };
        }

        http.Response.StatusCode = status;
        return await problems.TryWriteAsync(new ProblemDetailsContext { HttpContext = http, ProblemDetails = problem, Exception = exception });
    }

    private static (int Status, string Code, string Title, string? Detail) Map(Exception ex) => ex switch
    {
        SegregationOfDutiesException e => (StatusCodes.Status403Forbidden, "sod_violation", "Segregation-of-duties rule violated.", e.Message),
        TenantViolationException => (StatusCodes.Status403Forbidden, "tenant_violation", "Access to this resource is not permitted.", null),
        TenantRequiredException => (StatusCodes.Status401Unauthorized, "tenant_required", "Authentication with a tenant is required.", null),
        DbUpdateConcurrencyException => (StatusCodes.Status409Conflict, "concurrency_conflict", "The resource was changed by someone else. Reload and retry.", null),
        InvalidCredentialsException => (StatusCodes.Status401Unauthorized, "invalid_credentials", "Sign-in failed.", "Check your details and try again."),
        NotFoundException => (StatusCodes.Status404NotFound, "not_found", "The resource was not found.", null),
        ConflictException e => (StatusCodes.Status409Conflict, "conflict", "The request conflicts with the current state.", e.Message),
        ValidationFailedException e => (e.StatusCode, "validation_failed", "The request is not acceptable.", e.Message),
        BadHttpRequestException e => (e.StatusCode, "bad_request", "The request is malformed.", null),
        _ => (StatusCodes.Status500InternalServerError, "internal_error", "An unexpected error occurred.", null),
    };
}

/// <summary>Adds the trace and correlation ids to every problem response so support can find the log and audit entries.</summary>
public static class ProblemDetailsCustomization
{
    public static void Customize(ProblemDetailsContext context)
    {
        var http = context.HttpContext;
        var problem = context.ProblemDetails;
        problem.Extensions["traceId"] = System.Diagnostics.Activity.Current?.TraceId.ToString() ?? http.TraceIdentifier;
        if (http.RequestServices.GetService<AuditContext>()?.CorrelationId is { } correlation)
        {
            problem.Extensions["correlationId"] = correlation;
        }

        if (!problem.Extensions.ContainsKey("errorCode"))
        {
            // Framework-generated problems (401/403/404/429...) get stable VPMS codes instead of generic RFC links.
            var code = problem.Status switch
            {
                400 => "bad_request",
                401 => "unauthenticated",
                403 => "forbidden",
                404 => "not_found",
                405 => "method_not_allowed",
                409 => "conflict",
                429 => "rate_limited",
                >= 500 => "internal_error",
                _ => "error",
            };
            problem.Type = ProblemDetailsExceptionHandler.TypeBase + code;
            problem.Extensions["errorCode"] = code;
        }
    }
}
