using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using Vpms.BuildingBlocks.Audit;
using Vpms.BuildingBlocks.Security;
using Vpms.Identity.Application;
using Vpms.Identity.Domain;

namespace Vpms.Identity.Infrastructure;

public sealed class RoleAssignmentService(IdentityDbContext db, ConflictingPermissionsMatrix matrix, TimeProvider clock, IAuditPort audit)
    : IRoleAssignmentService
{
    public async Task EnsureSystemRolesAsync(CancellationToken ct = default)
    {
        var existing = await db.Roles.Select(r => r.Name).ToListAsync(ct);
        foreach (var (name, permissions) in SystemRoles.Definitions.Where(d => !existing.Contains(d.Key)))
        {
            db.Roles.Add(new Role
            {
                Id = Guid.NewGuid(), Name = name, IsSystem = true,
                Permissions = permissions.Select(p => new RolePermission { Id = Guid.NewGuid(), Permission = p }).ToList(),
            });
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task AssignAsync(Guid actorUserId, Guid userId, Guid roleId, Guid? legalEntityId, CancellationToken ct = default)
    {
        var role = await db.Roles.Include(r => r.Permissions).FirstOrDefaultAsync(r => r.Id == roleId, ct)
                   ?? throw new InvalidOperationException("Role not found.");
        _ = await db.Users.AnyAsync(u => u.Id == userId, ct)
            ? 0 : throw new InvalidOperationException("User not found.");

        // Permissions the user already holds that overlap the new scope: tenant-wide overlaps with everything.
        var held = await (
            from a in db.RoleAssignments
            join p in db.RolePermissions on a.RoleId equals p.RoleId
            where a.UserId == userId
                  && (legalEntityId == null || a.LegalEntityId == null || a.LegalEntityId == legalEntityId)
            select p.Permission).Distinct().ToListAsync(ct);

        var combined = held.Concat(role.Permissions.Select(p => p.Permission)).ToHashSet();
        await EnsureNoConflictsAudited(combined, actorUserId, userId, roleId, legalEntityId, ct);

        // A tenant-wide grant also overlaps every entity-scoped grant the user has.
        if (legalEntityId is null)
        {
            var scoped = await (
                from a in db.RoleAssignments
                join p in db.RolePermissions on a.RoleId equals p.RoleId
                where a.UserId == userId && a.LegalEntityId != null
                select new { a.LegalEntityId, p.Permission }).ToListAsync(ct);
            foreach (var group in scoped.GroupBy(x => x.LegalEntityId))
            {
                await EnsureNoConflictsAudited(group.Select(x => x.Permission).Concat(combined).ToHashSet(),
                    actorUserId, userId, roleId, legalEntityId, ct);
            }
        }

        var assignment = new RoleAssignment
        {
            Id = Guid.NewGuid(), UserId = userId, RoleId = roleId, LegalEntityId = legalEntityId,
            AssignedAt = clock.GetUtcNow(), AssignedBy = actorUserId,
        };
        db.RoleAssignments.Add(assignment);
        audit.Record(new AuditEntry("role.assigned", "RoleAssignment", assignment.Id.ToString(), legalEntityId,
            JsonSerializer.Serialize(new { userId, roleId, role = role.Name }), ActorId: actorUserId));
        await db.SaveChangesAsync(ct);
    }

    private async Task EnsureNoConflictsAudited(IReadOnlySet<string> permissions, Guid actorUserId, Guid userId, Guid roleId,
        Guid? legalEntityId, CancellationToken ct)
    {
        try
        {
            matrix.EnsureNoConflicts(permissions);
        }
        catch (SegregationOfDutiesException ex)
        {
            await audit.WriteNowAsync(new AuditEntry("sod.assignment.blocked", "RoleAssignment", null, legalEntityId,
                JsonSerializer.Serialize(new { userId, roleId, rule = ex.Rule }), ActorId: actorUserId), ct);
            throw;
        }
    }
}
