using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vpms.BuildingBlocks.Audit;
using Vpms.BuildingBlocks.Errors;
using Vpms.BuildingBlocks.Security;
using Vpms.CustomerAdmin.Contracts;
using Vpms.Identity.Application;
using Vpms.Identity.Contracts;
using Vpms.Identity.Domain;

namespace Vpms.Identity.Infrastructure;

public sealed class RoleAdminService(
    IdentityDbContext db, IRoleAssignmentService assignments, ConflictingPermissionsMatrix matrix, ICurrentUser current,
    ILegalEntityLookup entities, IAuditPort audit, TimeProvider clock) : IRoleAdminService
{
    public async Task<IReadOnlyList<RoleDto>> ListRolesAsync(CancellationToken ct = default)
    {
        var roles = await db.Roles.AsNoTracking().Include(r => r.Permissions).OrderBy(r => r.Name).ToListAsync(ct);
        var counts = await db.RoleAssignments.AsNoTracking().GroupBy(a => a.RoleId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        return roles.Select(r => Map(r, counts.GetValueOrDefault(r.Id))).ToList();
    }

    public async Task<RoleDto> CreateRoleAsync(string name, IReadOnlyCollection<string> permissions, CancellationToken ct = default)
    {
        var trimmed = (name ?? "").Trim();
        if (trimmed.Length is 0 or > 100)
        {
            throw new ValidationFailedException("invalid_name", "The role name is required (max 100 characters).");
        }

        var set = ValidatePermissions(permissions);
        if (await db.Roles.AnyAsync(r => r.Name == trimmed, ct))
        {
            throw new ConflictException("role_name_exists", "A role with this name already exists.");
        }

        var role = new Role { Id = Guid.NewGuid(), Name = trimmed, IsSystem = false, Permissions = set.Select(p => new RolePermission { Id = Guid.NewGuid(), Permission = p }).ToList() };
        db.Roles.Add(role);
        audit.Record(new AuditEntry("role.created", "Role", role.Id.ToString(), ChangeSummaryJson: JsonSerializer.Serialize(new { role.Name, permissions = set.Order() })));
        await db.SaveChangesAsync(ct);
        return Map(role, 0);
    }

    public async Task<RoleDto> UpdateRolePermissionsAsync(Guid roleId, IReadOnlyCollection<string> permissions, CancellationToken ct = default)
    {
        var role = await db.Roles.Include(r => r.Permissions).FirstOrDefaultAsync(r => r.Id == roleId, ct) ?? throw new NotFoundException("Role");
        if (role.IsSystem)
        {
            throw new ConflictException("system_role", "System roles cannot be changed.");
        }

        if (await db.RoleAssignments.AnyAsync(a => a.RoleId == roleId, ct))
        {
            throw new ConflictException("role_in_use", "This role is assigned to people. Create a new role and move them over.");
        }

        var set = ValidatePermissions(permissions);
        var before = role.Permissions.Select(p => p.Permission).Order().ToList();
        // New rows are added explicitly: entities with client-generated keys reached through a navigation would look "existing" to EF.
        db.RolePermissions.RemoveRange(role.Permissions);
        db.RolePermissions.AddRange(set.Select(p => new RolePermission { Id = Guid.NewGuid(), RoleId = roleId, Permission = p }));
        audit.Record(new AuditEntry("role.updated", "Role", roleId.ToString(), ChangeSummaryJson: JsonSerializer.Serialize(new { before, after = set.Order() })));
        await db.SaveChangesAsync(ct);
        return Map(await db.Roles.AsNoTracking().Include(r => r.Permissions).FirstAsync(r => r.Id == roleId, ct), 0);
    }

    public async Task DeleteRoleAsync(Guid roleId, CancellationToken ct = default)
    {
        var role = await db.Roles.FirstOrDefaultAsync(r => r.Id == roleId, ct) ?? throw new NotFoundException("Role");
        if (role.IsSystem)
        {
            throw new ConflictException("system_role", "System roles cannot be deleted.");
        }

        if (await db.RoleAssignments.AnyAsync(a => a.RoleId == roleId, ct) || await db.PendingRoleAssignments.AnyAsync(a => a.RoleId == roleId, ct))
        {
            throw new ConflictException("role_in_use", "This role is still assigned.");
        }

        db.Roles.Remove(role);
        audit.Record(new AuditEntry("role.deleted", "Role", roleId.ToString(), ChangeSummaryJson: JsonSerializer.Serialize(new { role.Name })));
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<RoleAssignmentDto>> ListAssignmentsAsync(Guid? userId, CancellationToken ct = default)
    {
        var q = from a in db.RoleAssignments.AsNoTracking()
                join r in db.Roles.AsNoTracking() on a.RoleId equals r.Id
                where userId == null || a.UserId == userId
                orderby a.AssignedAt
                select new RoleAssignmentDto(a.Id, a.UserId, a.RoleId, r.Name, a.LegalEntityId, a.AssignedAt, a.AssignedBy);
        return await q.ToListAsync(ct);
    }

    public async Task<RoleAssignmentDto> AssignAsync(Guid userId, Guid roleId, Guid? legalEntityId, CancellationToken ct = default)
    {
        // Nobody grants themselves access: a second person must do it (maker-checker).
        MakerCheckerGuard.EnsureDifferent(current.UserId, userId, "assign a role");

        if (legalEntityId is { } entity && !await entities.ExistsAsync(entity, ct))
        {
            throw new ValidationFailedException("unknown_legal_entity", "The legal entity does not exist.");
        }

        if (await db.RoleAssignments.AnyAsync(a => a.UserId == userId && a.RoleId == roleId && a.LegalEntityId == legalEntityId, ct))
        {
            throw new ConflictException("assignment_exists", "The person already has this role for that scope.");
        }

        await assignments.AssignAsync(current.UserId, userId, roleId, legalEntityId, ct);
        return (await ListAssignmentsAsync(userId, ct)).Last(a => a.RoleId == roleId && a.LegalEntityId == legalEntityId);
    }

    public async Task RevokeAsync(Guid assignmentId, CancellationToken ct = default)
    {
        var assignment = await db.RoleAssignments.FirstOrDefaultAsync(a => a.Id == assignmentId, ct) ?? throw new NotFoundException("Role assignment");
        if (assignment.LegalEntityId is null && await GrantsTenantAdminAsync(assignment.RoleId, ct)
            && !await AnotherActiveAdminExistsAsync(assignment.UserId, ct))
        {
            throw new ConflictException("last_tenant_admin", "At least one active tenant administrator must remain.");
        }

        db.RoleAssignments.Remove(assignment);
        audit.Record(new AuditEntry("role.revoked", "RoleAssignment", assignmentId.ToString(), assignment.LegalEntityId,
            JsonSerializer.Serialize(new { assignment.UserId, assignment.RoleId })));
        await db.SaveChangesAsync(ct);
    }

    public async Task<PendingAssignmentDto> AddPendingAssignmentAsync(string email, Guid roleId, Guid? legalEntityId, CancellationToken ct = default)
    {
        var normalized = (email ?? "").Trim().ToLowerInvariant();
        if (normalized.Length > 320 || !System.Text.RegularExpressions.Regex.IsMatch(normalized, @"^[^@\s]+@[^@\s]+\.[^@\s]+$"))
        {
            throw new ValidationFailedException("invalid_email", "Enter a valid email address.");
        }

        var role = await db.Roles.AsNoTracking().FirstOrDefaultAsync(r => r.Id == roleId, ct) ?? throw new NotFoundException("Role");
        if (legalEntityId is { } entity && !await entities.ExistsAsync(entity, ct))
        {
            throw new ValidationFailedException("unknown_legal_entity", "The legal entity does not exist.");
        }

        if (await db.PendingRoleAssignments.AnyAsync(p => p.Email == normalized && p.RoleId == roleId && p.LegalEntityId == legalEntityId, ct))
        {
            throw new ConflictException("assignment_exists", "This role is already prepared for that email.");
        }

        var row = new PendingRoleAssignment { Id = Guid.NewGuid(), Email = normalized, RoleId = roleId, LegalEntityId = legalEntityId, CreatedBy = current.UserId, CreatedAt = clock.GetUtcNow() };
        db.PendingRoleAssignments.Add(row);
        audit.Record(new AuditEntry("role.pending-added", "PendingRoleAssignment", row.Id.ToString(), legalEntityId,
            JsonSerializer.Serialize(new { roleId, role = role.Name })));
        await db.SaveChangesAsync(ct);
        return new PendingAssignmentDto(row.Id, normalized, roleId, role.Name, legalEntityId);
    }

    public async Task<IReadOnlyList<PendingAssignmentDto>> ListPendingAssignmentsAsync(CancellationToken ct = default) =>
        await (from p in db.PendingRoleAssignments.AsNoTracking()
               join r in db.Roles.AsNoTracking() on p.RoleId equals r.Id
               orderby p.Email
               select new PendingAssignmentDto(p.Id, p.Email, p.RoleId, r.Name, p.LegalEntityId)).ToListAsync(ct);

    private HashSet<string> ValidatePermissions(IReadOnlyCollection<string>? permissions)
    {
        if (permissions is null || permissions.Count == 0)
        {
            throw new ValidationFailedException("no_permissions", "A role needs at least one permission.");
        }

        var set = permissions.Select(p => p.Trim()).ToHashSet();
        var unknown = set.Where(p => !Permissions.All.Contains(p)).ToList();
        if (unknown.Count > 0)
        {
            throw new ValidationFailedException("unknown_permission", $"Unknown permission: {string.Join(", ", unknown)}.");
        }

        matrix.EnsureNoConflicts(set); // a single role must not bundle conflicting duties
        return set;
    }

    private async Task<bool> GrantsTenantAdminAsync(Guid roleId, CancellationToken ct) =>
        await db.RolePermissions.AnyAsync(p => p.RoleId == roleId && p.Permission == Permissions.TenantAdmin, ct);

    private async Task<bool> AnotherActiveAdminExistsAsync(Guid excludingUserId, CancellationToken ct) =>
        await (from a in db.RoleAssignments
               join p in db.RolePermissions on a.RoleId equals p.RoleId
               join u in db.Users on a.UserId equals u.Id
               where p.Permission == Permissions.TenantAdmin && a.LegalEntityId == null && u.Status == UserStatus.Active && a.UserId != excludingUserId
               select a.Id).AnyAsync(ct);

    private static RoleDto Map(Role r, int count) => new(r.Id, r.Name, r.IsSystem, r.Permissions.Select(p => p.Permission).Order().ToList(), count);
}
