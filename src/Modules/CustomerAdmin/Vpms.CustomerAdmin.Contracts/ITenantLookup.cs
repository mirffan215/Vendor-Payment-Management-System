namespace Vpms.CustomerAdmin.Contracts;

public sealed record TenantDefaults(Guid TenantId, string Locale, string Calendar);

/// <summary>Resolves a tenant from its public slug before any tenant context exists (login pages).</summary>
public interface ITenantLookup
{
    Task<Guid?> FindTenantIdBySlugAsync(string slug, CancellationToken ct = default);

    Task<string?> FindSlugAsync(Guid tenantId, CancellationToken ct = default);

    /// <summary>Active tenants with their language and calendar defaults, for background jobs that visit every tenant.</summary>
    Task<IReadOnlyList<TenantDefaults>> ListActiveAsync(CancellationToken ct = default);

    Task<TenantDefaults?> GetDefaultsAsync(Guid tenantId, CancellationToken ct = default);
}
