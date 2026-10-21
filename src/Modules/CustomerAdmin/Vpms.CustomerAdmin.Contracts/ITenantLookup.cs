namespace Vpms.CustomerAdmin.Contracts;

/// <summary>Resolves a tenant from its public slug before any tenant context exists (login pages).</summary>
public interface ITenantLookup
{
    Task<Guid?> FindTenantIdBySlugAsync(string slug, CancellationToken ct = default);
}
