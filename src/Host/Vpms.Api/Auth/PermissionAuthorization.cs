using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Vpms.Identity.Contracts;

namespace Vpms.Api.Auth;

public sealed record PermissionRequirement(string Permission) : IAuthorizationRequirement;

/// <summary>
/// Evaluates a permission for the signed-in user. A legalEntityId route or query value narrows the check to that
/// entity; without one the permission must be held tenant-wide.
/// </summary>
public sealed class PermissionHandler(IPermissionEvaluator evaluator, IHttpContextAccessor http)
    : AuthorizationHandler<PermissionRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        if (!Guid.TryParse(context.User.FindFirstValue(VpmsClaims.UserId), out var userId))
        {
            return;
        }

        Guid? entityId = null;
        var request = http.HttpContext?.Request;
        var raw = request?.RouteValues["legalEntityId"]?.ToString() ?? request?.Query["legalEntityId"].ToString();
        if (!string.IsNullOrEmpty(raw))
        {
            if (!Guid.TryParse(raw, out var parsed))
            {
                return;
            }

            entityId = parsed;
        }

        if (await evaluator.HasAsync(userId, requirement.Permission, entityId, http.HttpContext?.RequestAborted ?? default))
        {
            context.Succeed(requirement);
        }
    }
}

public static class PermissionPolicies
{
    /// <summary>One policy per catalogue permission, named by the permission itself.</summary>
    public static AuthorizationBuilder AddPermissionPolicies(this AuthorizationBuilder builder)
    {
        foreach (var permission in Permissions.All)
        {
            builder.AddPolicy(permission, p => p
                .RequireAuthenticatedUser()
                .RequireClaim(VpmsClaims.Context, VpmsClaims.InternalContext)
                .AddRequirements(new PermissionRequirement(permission)));
        }

        return builder;
    }
}
