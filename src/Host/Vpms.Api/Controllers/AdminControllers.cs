using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Vpms.Api.Auth;
using Vpms.CustomerAdmin.Application;
using Vpms.Identity.Application;
using Vpms.Identity.Contracts;
using Vpms.Identity.Domain;

namespace Vpms.Api.Controllers;

/// <summary>Resolves "which legal entities may this user act on for this permission" for list endpoints.</summary>
public static class ScopeExtensions
{
    public static async Task<(bool Allowed, IReadOnlySet<Guid>? Scope)> ScopeForAsync(
        this ControllerBase controller, IPermissionEvaluator evaluator, string permission, CancellationToken ct)
    {
        var userId = Guid.Parse(controller.User.FindFirst(VpmsClaims.UserId)!.Value);
        var scope = await evaluator.GetScopeAsync(userId, permission, ct);
        return scope == PermissionScope.None ? (false, null) : (true, scope.AllEntities ? null : scope.EntityIds);
    }
}

[ApiController, ApiVersion("1.0"), Route("api/v{version:apiVersion}/admin/tenant")]
public sealed class TenantProfileController(ITenantProfileService service) : ControllerBase
{
    [HttpGet]
    public async Task<TenantProfile> Get(CancellationToken ct) => await service.GetAsync(ct);

    [HttpPut, Authorize(Policy = Permissions.BrandingManage)]
    public async Task<TenantProfile> Update(UpdateTenantProfile body, CancellationToken ct) => await service.UpdateAsync(body, ct);
}

[ApiController, ApiVersion("1.0"), Route("api/v{version:apiVersion}/admin/legal-entities")]
public sealed class LegalEntitiesController(ILegalEntityService service, IPermissionEvaluator permissions) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var (allowed, scope) = await this.ScopeForAsync(permissions, Permissions.EntityView, ct);
        return allowed ? Ok(await service.ListAsync(scope, ct)) : Forbid();
    }

    [HttpGet("{legalEntityId:guid}"), Authorize(Policy = Permissions.EntityView)]
    public async Task<LegalEntityDto> Get(Guid legalEntityId, CancellationToken ct) => await service.GetAsync(legalEntityId, ct);

    [HttpPost, Authorize(Policy = Permissions.EntityManage)]
    public async Task<IActionResult> Create(CreateLegalEntity body, CancellationToken ct)
    {
        var created = await service.CreateAsync(body, ct);
        return CreatedAtAction(nameof(Get), new { legalEntityId = created.Id }, created);
    }

    [HttpPut("{legalEntityId:guid}"), Authorize(Policy = Permissions.EntityManage)]
    public async Task<LegalEntityDto> Update(Guid legalEntityId, UpdateLegalEntity body, CancellationToken ct) =>
        await service.UpdateAsync(legalEntityId, body, ct);

    [HttpPost("{legalEntityId:guid}/activate"), Authorize(Policy = Permissions.EntityManage)]
    public async Task<LegalEntityDto> Activate(Guid legalEntityId, CancellationToken ct) => await service.SetStatusAsync(legalEntityId, true, ct);

    [HttpPost("{legalEntityId:guid}/deactivate"), Authorize(Policy = Permissions.EntityManage)]
    public async Task<LegalEntityDto> Deactivate(Guid legalEntityId, CancellationToken ct) => await service.SetStatusAsync(legalEntityId, false, ct);
}

public sealed record RoleRequest(string Name, IReadOnlyList<string> Permissions);

public sealed record PermissionsRequest(IReadOnlyList<string> Permissions);

public sealed record AssignRequest(Guid UserId, Guid RoleId, Guid? LegalEntityId);

public sealed record PendingRequest(string Email, Guid RoleId, Guid? LegalEntityId);

[ApiController, ApiVersion("1.0"), Route("api/v{version:apiVersion}/admin"), Authorize(Policy = Permissions.RoleManage)]
public sealed class RolesController(IRoleAdminService service) : ControllerBase
{
    [HttpGet("roles")]
    public async Task<IReadOnlyList<RoleDto>> Roles(CancellationToken ct) => await service.ListRolesAsync(ct);

    [HttpGet("permissions")]
    public IReadOnlyList<string> PermissionCatalogue() => Permissions.All;

    [HttpPost("roles")]
    [Vpms.Api.Auth.RequireStepUp]
    public async Task<RoleDto> Create(RoleRequest body, CancellationToken ct) => await service.CreateRoleAsync(body.Name, body.Permissions, ct);

    [HttpPut("roles/{roleId:guid}/permissions")]
    [Vpms.Api.Auth.RequireStepUp]
    public async Task<RoleDto> Update(Guid roleId, PermissionsRequest body, CancellationToken ct) =>
        await service.UpdateRolePermissionsAsync(roleId, body.Permissions, ct);

    [HttpDelete("roles/{roleId:guid}")]
    [Vpms.Api.Auth.RequireStepUp]
    public async Task<IActionResult> Delete(Guid roleId, CancellationToken ct)
    {
        await service.DeleteRoleAsync(roleId, ct);
        return NoContent();
    }

    [HttpGet("role-assignments")]
    public async Task<IReadOnlyList<RoleAssignmentDto>> Assignments([FromQuery] Guid? userId, CancellationToken ct) =>
        await service.ListAssignmentsAsync(userId, ct);

    [HttpPost("role-assignments")]
    [Vpms.Api.Auth.RequireStepUp]
    public async Task<RoleAssignmentDto> Assign(AssignRequest body, CancellationToken ct) =>
        await service.AssignAsync(body.UserId, body.RoleId, body.LegalEntityId, ct);

    [HttpDelete("role-assignments/{assignmentId:guid}")]
    [Vpms.Api.Auth.RequireStepUp]
    public async Task<IActionResult> Revoke(Guid assignmentId, CancellationToken ct)
    {
        await service.RevokeAsync(assignmentId, ct);
        return NoContent();
    }

    [HttpGet("pending-role-assignments")]
    public async Task<IReadOnlyList<PendingAssignmentDto>> Pending(CancellationToken ct) => await service.ListPendingAssignmentsAsync(ct);

    [HttpPost("pending-role-assignments")]
    [Vpms.Api.Auth.RequireStepUp]
    public async Task<PendingAssignmentDto> AddPending(PendingRequest body, CancellationToken ct) =>
        await service.AddPendingAssignmentAsync(body.Email, body.RoleId, body.LegalEntityId, ct);
}

[ApiController, ApiVersion("1.0"), Route("api/v{version:apiVersion}/admin/users"), Authorize(Policy = Permissions.UserManage)]
public sealed class UsersController(IUserAdminService service) : ControllerBase
{
    [HttpGet]
    public async Task<IReadOnlyList<UserDto>> List([FromQuery] string? search, [FromQuery, System.ComponentModel.DataAnnotations.Range(0, 1_000_000)] int skip = 0, [FromQuery, System.ComponentModel.DataAnnotations.Range(1, 200)] int take = 50, CancellationToken ct = default) =>
        await service.ListAsync(search, skip, take, ct);

    [HttpPost("{userId:guid}/enable")]
    public async Task<UserDto> Enable(Guid userId, CancellationToken ct) => await service.SetStatusAsync(userId, true, ct);

    [HttpPost("{userId:guid}/disable")]
    public async Task<UserDto> Disable(Guid userId, CancellationToken ct) => await service.SetStatusAsync(userId, false, ct);

    /// <summary>Ends all of a person's sessions (a lost laptop, a suspected account takeover). Takes effect within a minute on every instance.</summary>
    [HttpPost("{userId:guid}/revoke-sessions")]
    public async Task<IActionResult> RevokeSessions(Guid userId, [FromServices] Microsoft.Extensions.Caching.Memory.IMemoryCache cache, CancellationToken ct)
    {
        await service.RevokeSessionsAsync(userId, ct);
        cache.Remove(Vpms.Api.Auth.CookieSessionValidator.StateKey(userId));
        return NoContent();
    }
}

[ApiController, ApiVersion("1.0"), Route("api/v{version:apiVersion}/admin/identity-providers"), Authorize(Policy = Permissions.ProviderManage)]
public sealed class IdentityProvidersController(IProviderAdminService service) : ControllerBase
{
    [HttpGet]
    public async Task<IReadOnlyList<ProviderDto>> List(CancellationToken ct) => await service.ListAsync(ct);

    [HttpPost]
    [Vpms.Api.Auth.RequireStepUp]
    public async Task<ProviderDto> Create(SaveProvider body, CancellationToken ct) => await service.CreateAsync(body, ct);

    [HttpPut("{providerId:guid}")]
    [Vpms.Api.Auth.RequireStepUp]
    public async Task<ProviderDto> Update(Guid providerId, SaveProvider body, CancellationToken ct) => await service.UpdateAsync(providerId, body, ct);

    [HttpPost("{providerId:guid}/enable")]
    [Vpms.Api.Auth.RequireStepUp]
    public async Task<ProviderDto> Enable(Guid providerId, CancellationToken ct) => await service.SetEnabledAsync(providerId, true, ct);

    [HttpPost("{providerId:guid}/disable")]
    [Vpms.Api.Auth.RequireStepUp]
    public async Task<ProviderDto> Disable(Guid providerId, CancellationToken ct) => await service.SetEnabledAsync(providerId, false, ct);
}

[ApiController, ApiVersion("1.0"), Route("api/v{version:apiVersion}/admin")]
public sealed class ApprovalAdminController(IApprovalAdminService service, IPermissionEvaluator permissions) : ControllerBase
{
    [HttpGet("approval-authorities")]
    public async Task<IActionResult> Authorities([FromQuery] Guid? legalEntityId, CancellationToken ct)
    {
        var (allowed, scope) = await this.ScopeForAsync(permissions, Permissions.AuthorityManage, ct);
        return !allowed || (legalEntityId is { } e && scope is not null && !scope.Contains(e))
            ? Forbid()
            : Ok(await service.ListAuthoritiesAsync(legalEntityId, scope, ct));
    }

    // The entity id is part of the body, so the entity-scoped permission is checked here rather than by route.
    [HttpPost("approval-authorities")]
    public async Task<IActionResult> CreateAuthority(CreateApprovalAuthority body, CancellationToken ct)
    {
        if (!await HasAsync(body.LegalEntityId, ct)) return Forbid();
        return Ok(await service.CreateAuthorityAsync(body, ct));
    }

    [HttpDelete("approval-authorities/{id:guid}"), Authorize(Policy = Permissions.AuthorityManage)]
    public async Task<IActionResult> RevokeAuthority(Guid id, CancellationToken ct)
    {
        await service.RevokeAuthorityAsync(id, ct);
        return NoContent();
    }

    [HttpGet("approval-rules")]
    public async Task<IActionResult> Rules([FromQuery] Guid? legalEntityId, CancellationToken ct)
    {
        var (allowed, scope) = await this.ScopeForAsync(permissions, Permissions.AuthorityManage, ct);
        return !allowed || (legalEntityId is { } e && scope is not null && !scope.Contains(e))
            ? Forbid()
            : Ok(await service.ListRulesAsync(legalEntityId, scope, ct));
    }

    [HttpPost("approval-rules")]
    public async Task<IActionResult> CreateRule(CreateApprovalMatrixRule body, CancellationToken ct)
    {
        if (!await HasAsync(body.LegalEntityId, ct)) return Forbid();
        return Ok(await service.CreateRuleAsync(body, ct));
    }

    [HttpDelete("approval-rules/{id:guid}"), Authorize(Policy = Permissions.AuthorityManage)]
    public async Task<IActionResult> DisableRule(Guid id, CancellationToken ct)
    {
        await service.DisableRuleAsync(id, ct);
        return NoContent();
    }

    private Task<bool> HasAsync(Guid legalEntityId, CancellationToken ct) =>
        permissions.HasAsync(Guid.Parse(User.FindFirst(VpmsClaims.UserId)!.Value), Permissions.AuthorityManage, legalEntityId, ct);
}
