using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vpms.BuildingBlocks.Audit;
using Vpms.BuildingBlocks.Errors;
using Vpms.BuildingBlocks.Security;
using Vpms.Identity.Application;
using Vpms.Identity.Contracts;
using Vpms.Identity.Domain;

namespace Vpms.Identity.Infrastructure;

public sealed class UserAdminService(IdentityDbContext db, ICurrentUser current, IAuditPort audit) : IUserAdminService, IUserDirectory, IUserPreferences
{
    public async Task<IReadOnlyList<UserDto>> ListAsync(string? search, int skip, int take, CancellationToken ct = default)
    {
        take = Math.Clamp(take, 1, 200);
        var q = db.Users.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            q = q.Where(u => u.Email.Contains(term) || u.DisplayName.Contains(term));
        }

        var users = await q.OrderBy(u => u.Email).Skip(Math.Max(0, skip)).Take(take).ToListAsync(ct);
        var ids = users.Select(u => u.Id).ToList();
        var roles = await (from a in db.RoleAssignments.AsNoTracking()
                           join r in db.Roles.AsNoTracking() on a.RoleId equals r.Id
                           where ids.Contains(a.UserId)
                           select new { a.UserId, r.Name }).ToListAsync(ct);
        return users.Select(u => new UserDto(u.Id, u.Email, u.DisplayName, u.Status, u.LastLoginAt,
            roles.Where(r => r.UserId == u.Id).Select(r => r.Name).Distinct().Order().ToList())).ToList();
    }

    public async Task<UserDto> SetStatusAsync(Guid userId, bool active, CancellationToken ct = default)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct) ?? throw new NotFoundException("User");
        var status = active ? UserStatus.Active : UserStatus.Disabled;
        if (user.Status != status)
        {
            if (!active)
            {
                MakerCheckerGuard.EnsureDifferent(current.UserId, userId, "disable a user");
                if (await IsLastAdminAsync(userId, ct))
                {
                    throw new ConflictException("last_tenant_admin", "At least one active tenant administrator must remain.");
                }
            }

            audit.Record(new AuditEntry(active ? "user.enabled" : "user.disabled", "User", userId.ToString(),
                ChangeSummaryJson: JsonSerializer.Serialize(new { from = user.Status, to = status })));
            user.Status = status;
            await db.SaveChangesAsync(ct);
        }

        return (await ListAsync(user.Email, 0, 1, ct)).First(u => u.Id == userId);
    }

    public Task<bool> ExistsAsync(Guid userId, CancellationToken ct = default) => db.Users.AnyAsync(u => u.Id == userId, ct);

    public async Task<IReadOnlyDictionary<Guid, UserSummary>> GetAsync(IEnumerable<Guid> userIds, CancellationToken ct = default)
    {
        var ids = userIds.Distinct().ToList();
        return await db.Users.AsNoTracking().Where(u => ids.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => new UserSummary(u.Id, u.Email, u.DisplayName, u.Status, u.Locale, u.Calendar), ct);
    }

    public async Task<UserPreferencesDto> GetAsync(Guid userId, CancellationToken ct = default)
    {
        var u = await db.Users.AsNoTracking().FirstOrDefaultAsync(x => x.Id == userId, ct) ?? throw new NotFoundException("User");
        return new UserPreferencesDto(u.Locale, u.Calendar);
    }

    public async Task<UserPreferencesDto> SetMineAsync(string locale, string calendar, CancellationToken ct = default)
    {
        if (locale is not ("en" or "ar")) throw new ValidationFailedException("invalid_locale", "Choose English or Arabic.");
        if (calendar is not ("gregorian" or "hijri")) throw new ValidationFailedException("invalid_calendar", "Choose the Gregorian or the Hijri calendar.");
        var userId = current.UserId;
        var u = await db.Users.FirstOrDefaultAsync(x => x.Id == userId, ct) ?? throw new NotFoundException("User");
        if (u.Locale != locale || u.Calendar != calendar)
        {
            u.Locale = locale;
            u.Calendar = calendar;
            audit.Record(new AuditEntry("user.preferences.changed", "User", userId.ToString(), null, JsonSerializer.Serialize(new { locale, calendar })));
            await db.SaveChangesAsync(ct);
        }

        return new UserPreferencesDto(locale, calendar);
    }

    private async Task<bool> IsLastAdminAsync(Guid userId, CancellationToken ct)
    {
        var holdsAdmin = await (from a in db.RoleAssignments
                                join p in db.RolePermissions on a.RoleId equals p.RoleId
                                where a.UserId == userId && p.Permission == Permissions.TenantAdmin && a.LegalEntityId == null
                                select a.Id).AnyAsync(ct);
        if (!holdsAdmin)
        {
            return false;
        }

        return !await (from a in db.RoleAssignments
                       join p in db.RolePermissions on a.RoleId equals p.RoleId
                       join u in db.Users on a.UserId equals u.Id
                       where p.Permission == Permissions.TenantAdmin && a.LegalEntityId == null && u.Status == UserStatus.Active && a.UserId != userId
                       select a.Id).AnyAsync(ct);
    }
}
