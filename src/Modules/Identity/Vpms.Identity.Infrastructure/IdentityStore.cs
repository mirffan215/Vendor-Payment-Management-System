using Microsoft.EntityFrameworkCore;
using Vpms.BuildingBlocks.Tenancy;
using Vpms.Identity.Application;
using Vpms.Identity.Domain;

namespace Vpms.Identity.Infrastructure;

public sealed class IdentityStore(IdentityDbContext db, TenantContext tenant) : IIdentityStore
{
    public async Task<IdentityProviderConfig?> FindProviderAsync(Guid providerId, CancellationToken ct = default)
    {
        using var _ = tenant.BeginPlatformScope();
        return await db.IdentityProviders.AsNoTracking().FirstOrDefaultAsync(p => p.Id == providerId, ct);
    }

    public async Task<IReadOnlyList<ProviderSummary>> ListEnabledProvidersAsync(Guid tenantId, CancellationToken ct = default)
    {
        using var _ = tenant.BeginPlatformScope();
        return await db.IdentityProviders.AsNoTracking()
            .Where(p => p.TenantId == tenantId && p.Enabled)
            .OrderBy(p => p.DisplayName)
            .Select(p => new ProviderSummary(p.Id, p.DisplayName, p.Kind))
            .ToListAsync(ct);
    }

    public Task<User?> FindUserAsync(Guid providerId, string externalSubject, CancellationToken ct = default) =>
        db.Users.FirstOrDefaultAsync(u => u.ProviderId == providerId && u.ExternalSubject == externalSubject, ct);

    public async Task AddUserAsync(User user, CancellationToken ct = default) => await db.Users.AddAsync(user, ct);

    public Task SaveChangesAsync(CancellationToken ct = default) => db.SaveChangesAsync(ct);

    public async Task<SessionState> GetSessionStateAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => new { u.Status, u.SessionsValidFrom }).FirstOrDefaultAsync(ct);
        return user is null ? new SessionState(false, null) : new SessionState(user.Status == UserStatus.Active, user.SessionsValidFrom);
    }
}
