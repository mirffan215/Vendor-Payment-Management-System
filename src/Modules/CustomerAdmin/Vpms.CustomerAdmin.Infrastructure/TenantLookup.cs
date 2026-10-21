using Microsoft.EntityFrameworkCore;
using Vpms.BuildingBlocks.Tenancy;
using Vpms.CustomerAdmin.Contracts;

namespace Vpms.CustomerAdmin.Infrastructure;

public sealed class TenantLookup(CustomerAdminDbContext db, TenantContext tenant) : ITenantLookup
{
    public async Task<Guid?> FindTenantIdBySlugAsync(string slug, CancellationToken ct = default)
    {
        using var _ = tenant.BeginPlatformScope();
        var normalized = slug.Trim().ToLowerInvariant();
        return await db.Tenants.AsNoTracking()
            .Where(t => t.Slug == normalized && t.Status == "Active")
            .Select(t => (Guid?)t.Id)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<string?> FindSlugAsync(Guid tenantId, CancellationToken ct = default)
    {
        using var _ = tenant.BeginPlatformScope();
        return await db.Tenants.AsNoTracking().Where(t => t.Id == tenantId).Select(t => t.Slug).FirstOrDefaultAsync(ct);
    }
}
