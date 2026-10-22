using Microsoft.EntityFrameworkCore;
using Vpms.Identity.Contracts;
using Vpms.Identity.Domain;

namespace Vpms.Identity.Infrastructure;

public sealed class ApproverDirectory(IdentityDbContext db) : IApproverDirectory
{
    public async Task<IReadOnlyDictionary<string, IReadOnlyList<Guid>>> GetRoleHoldersAsync(Guid legalEntityId, IEnumerable<string> roleNames, CancellationToken ct = default)
    {
        var names = roleNames.Distinct().ToList();
        var rows = await (
            from a in db.RoleAssignments
            join r in db.Roles on a.RoleId equals r.Id
            join u in db.Users on a.UserId equals u.Id
            where names.Contains(r.Name) && (a.LegalEntityId == null || a.LegalEntityId == legalEntityId) && u.Status == UserStatus.Active
            select new { r.Name, a.UserId }).Distinct().ToListAsync(ct);

        return names.ToDictionary(n => n, n => (IReadOnlyList<Guid>)rows.Where(x => x.Name == n).Select(x => x.UserId).OrderBy(x => x).ToList());
    }

    public async Task<IReadOnlyList<Guid>> ListPermissionHoldersAsync(Guid legalEntityId, string permission, CancellationToken ct = default) =>
        await (
            from a in db.RoleAssignments
            join p in db.RolePermissions on a.RoleId equals p.RoleId
            join u in db.Users on a.UserId equals u.Id
            where p.Permission == permission && (a.LegalEntityId == null || a.LegalEntityId == legalEntityId) && u.Status == UserStatus.Active
            select a.UserId).Distinct().ToListAsync(ct);

    public async Task<IReadOnlySet<Guid>> FilterByPermissionAsync(Guid legalEntityId, string permission, IEnumerable<Guid> userIds, CancellationToken ct = default)
    {
        var ids = userIds.Distinct().ToList();
        var holders = await (
            from a in db.RoleAssignments
            join p in db.RolePermissions on a.RoleId equals p.RoleId
            join u in db.Users on a.UserId equals u.Id
            where ids.Contains(a.UserId) && p.Permission == permission && (a.LegalEntityId == null || a.LegalEntityId == legalEntityId) && u.Status == UserStatus.Active
            select a.UserId).Distinct().ToListAsync(ct);
        return holders.ToHashSet();
    }
}
