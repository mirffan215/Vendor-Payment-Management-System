using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Vpms.BuildingBlocks.Tenancy;

namespace Vpms.BuildingBlocks.Security.Encryption;

public sealed record UnwrappedKey(int Version, byte[] Material);

/// <summary>
/// Singleton cache of unwrapped tenant keys. Keys are created on first use. Unwrapped material lives only in memory
/// and expires, so a rotation or revocation takes effect within the cache duration.
/// </summary>
public sealed class TenantKeyRing(
    IServiceScopeFactory scopes,
    IKeyEncryptionKey kek,
    IOptions<EncryptionOptions> options,
    TimeProvider clock) : ITenantKeyManager
{
    private sealed record Entry(IReadOnlyList<UnwrappedKey> Keys, int ActiveVersion, DateTimeOffset Expires);

    private readonly ConcurrentDictionary<(Guid Tenant, string Purpose), Entry> _cache = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<UnwrappedKey> GetActiveAsync(Guid tenantId, string purpose, CancellationToken ct = default)
    {
        var entry = await LoadAsync(tenantId, purpose, ct);
        return entry.Keys.First(k => k.Version == entry.ActiveVersion);
    }

    public async Task<UnwrappedKey> GetAsync(Guid tenantId, string purpose, int version, CancellationToken ct = default)
    {
        var entry = await LoadAsync(tenantId, purpose, ct);
        return entry.Keys.FirstOrDefault(k => k.Version == version)
               ?? throw new EncryptionException($"Key version {version} is not available for this tenant.");
    }

    public async Task<int> RotateDataKeyAsync(Guid tenantId, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var entry = await LoadAsync(tenantId, KeyPurpose.Data, ct, forceReload: true);
            var next = entry.Keys.Max(k => k.Version) + 1;
            var material = RandomNumberGenerator.GetBytes(32);
            var stored = new StoredTenantKey(tenantId, KeyPurpose.Data, next, KeyStatus.Active, kek.KeyId, await kek.WrapAsync(material, ct));

            await using var scope = NewScope(tenantId);
            var store = scope.ServiceProvider.GetRequiredService<ITenantKeyStore>();
            if (!await store.TryAddAsync(stored, entry.ActiveVersion, ct))
            {
                throw new EncryptionException("Another rotation is already in progress.");
            }

            _cache.TryRemove((tenantId, KeyPurpose.Data), out _);
            return next;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<Entry> LoadAsync(Guid tenantId, string purpose, CancellationToken ct, bool forceReload = false)
    {
        if (!forceReload && _cache.TryGetValue((tenantId, purpose), out var cached) && cached.Expires > clock.GetUtcNow())
        {
            return cached;
        }

        await using var scope = NewScope(tenantId);
        var store = scope.ServiceProvider.GetRequiredService<ITenantKeyStore>();
        var stored = (await store.ListAsync(tenantId, ct)).Where(k => k.Purpose == purpose).ToList();

        if (stored.Count == 0)
        {
            var created = new StoredTenantKey(tenantId, purpose, 1, KeyStatus.Active, kek.KeyId, await kek.WrapAsync(RandomNumberGenerator.GetBytes(32), ct));
            await store.TryAddAsync(created, null, ct);
            stored = (await store.ListAsync(tenantId, ct)).Where(k => k.Purpose == purpose).ToList();
        }

        var keys = new List<UnwrappedKey>();
        foreach (var k in stored)
        {
            keys.Add(new UnwrappedKey(k.Version, await kek.UnwrapAsync(k.WrappedKey, k.KekId, ct)));
        }

        var active = stored.Where(k => k.Status == KeyStatus.Active).Select(k => k.Version).DefaultIfEmpty(-1).Max();
        if (active < 0)
        {
            throw new EncryptionException("Tenant has no active key.");
        }

        var entry = new Entry(keys, active, clock.GetUtcNow() + options.Value.KeyCacheDuration);
        _cache[(tenantId, purpose)] = entry;
        return entry;
    }

    private AsyncServiceScope NewScope(Guid tenantId)
    {
        var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().SetTenant(tenantId);
        return scope;
    }
}
