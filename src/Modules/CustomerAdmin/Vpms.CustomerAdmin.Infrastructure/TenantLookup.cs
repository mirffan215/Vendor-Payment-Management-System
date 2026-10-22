using Microsoft.EntityFrameworkCore;
using Vpms.BuildingBlocks.Tenancy;
using Vpms.CustomerAdmin.Contracts;
using Vpms.CustomerAdmin.Domain;

namespace Vpms.CustomerAdmin.Infrastructure;

public sealed class TenantLookup(CustomerAdminDbContext db, TenantContext tenant) : ITenantLookup
{
    public async Task<Guid?> FindTenantIdBySlugAsync(string slug, CancellationToken ct = default)
    {
        using var _ = tenant.BeginPlatformScope();
        var normalized = slug.Trim().ToLowerInvariant();
        return await db.Tenants.AsNoTracking()
            .Where(t => t.Slug == normalized && t.Status == TenantStatus.Active)
            .Select(t => (Guid?)t.Id)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<IReadOnlyList<TenantDefaults>> ListActiveAsync(CancellationToken ct = default)
    {
        using var _ = tenant.BeginPlatformScope();
        return await db.Tenants.AsNoTracking().Where(t => t.Status == TenantStatus.Active).Select(t => new TenantDefaults(t.Id, t.DefaultLocale, t.DefaultCalendar)).ToListAsync(ct);
    }

    public async Task<bool> IsActiveAsync(Guid tenantId, CancellationToken ct = default)
    {
        using var _ = tenant.BeginPlatformScope();
        return await db.Tenants.AsNoTracking().AnyAsync(t => t.Id == tenantId && t.Status == TenantStatus.Active, ct);
    }

    public async Task<TenantDefaults?> GetDefaultsAsync(Guid tenantId, CancellationToken ct = default)
    {
        using var _ = tenant.BeginPlatformScope();
        return await db.Tenants.AsNoTracking().Where(t => t.Id == tenantId).Select(t => new TenantDefaults(t.Id, t.DefaultLocale, t.DefaultCalendar)).FirstOrDefaultAsync(ct);
    }

    public async Task<string?> FindSlugAsync(Guid tenantId, CancellationToken ct = default)
    {
        using var _ = tenant.BeginPlatformScope();
        return await db.Tenants.AsNoTracking().Where(t => t.Id == tenantId).Select(t => t.Slug).FirstOrDefaultAsync(ct);
    }
}
