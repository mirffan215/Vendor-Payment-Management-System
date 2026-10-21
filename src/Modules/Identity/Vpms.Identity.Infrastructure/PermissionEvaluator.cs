using Microsoft.EntityFrameworkCore;
using Vpms.Identity.Contracts;

namespace Vpms.Identity.Infrastructure;

public sealed class PermissionEvaluator(IdentityDbContext db) : IPermissionEvaluator
{
    public async Task<bool> HasAsync(Guid userId, string permission, Guid? legalEntityId = null, CancellationToken ct = default)
    {
        var scope = await GetScopeAsync(userId, permission, ct);
        return legalEntityId is null ? scope.AllEntities : scope.Allows(legalEntityId);
    }

    public async Task<PermissionScope> GetScopeAsync(Guid userId, string permission, CancellationToken ct = default)
    {
        // The tenant query filter applies to every table here, so only the current tenant's assignments are visible.
        var entityIds = await (
            from a in db.RoleAssignments
            join p in db.RolePermissions on a.RoleId equals p.RoleId
            where a.UserId == userId && p.Permission == permission
            select a.LegalEntityId).Distinct().ToListAsync(ct);

        if (entityIds.Count == 0)
        {
            return PermissionScope.None;
        }

        return entityIds.Contains(null)
            ? new PermissionScope(true, new HashSet<Guid>())
            : new PermissionScope(false, entityIds.Select(e => e!.Value).ToHashSet());
    }
}
