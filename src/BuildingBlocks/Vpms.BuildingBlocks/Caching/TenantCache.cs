using Microsoft.Extensions.Caching.Distributed;
using Vpms.BuildingBlocks.Tenancy;

namespace Vpms.BuildingBlocks.Caching;

public interface ITenantCache
{
    Task<byte[]?> GetAsync(string key, CancellationToken ct = default);
    Task SetAsync(string key, byte[] value, TimeSpan ttl, CancellationToken ct = default);
    Task RemoveAsync(string key, CancellationToken ct = default);
}

/// <summary>All keys are prefixed t:{tenantId}: so tenants can never read each other's entries.</summary>
public sealed class TenantCache(IDistributedCache inner, ITenantContext tenant) : ITenantCache
{
    public Task<byte[]?> GetAsync(string key, CancellationToken ct = default) => inner.GetAsync(Key(key), ct);

    public Task SetAsync(string key, byte[] value, TimeSpan ttl, CancellationToken ct = default) =>
        inner.SetAsync(Key(key), value, new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = ttl }, ct);

    public Task RemoveAsync(string key, CancellationToken ct = default) => inner.RemoveAsync(Key(key), ct);

    private string Key(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("Cache key is required.", nameof(key));
        }

        return $"t:{tenant.RequireTenantId():N}:{key}";
    }
}
