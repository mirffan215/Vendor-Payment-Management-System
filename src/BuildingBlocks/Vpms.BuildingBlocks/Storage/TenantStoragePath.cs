using Vpms.BuildingBlocks.Tenancy;

namespace Vpms.BuildingBlocks.Storage;

/// <summary>Builds storage keys of the form {tenantId}/{legalEntityId|_}/{kind}/{id}/{file}; rejects anything unsafe.</summary>
public static class TenantStoragePath
{
    public static string Build(Guid tenantId, Guid? legalEntityId, string kind, Guid id, string fileName)
    {
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("Tenant id is required.", nameof(tenantId));
        }

        return string.Join('/',
            tenantId.ToString("N"),
            legalEntityId?.ToString("N") ?? "_",
            Segment(kind, nameof(kind)),
            id.ToString("N"),
            Segment(fileName, nameof(fileName)));
    }

    private static string Segment(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value is "." or ".." || value.Length > 200 ||
            value.Any(c => c is '/' or '\\' or ':' || char.IsControl(c)))
        {
            throw new ArgumentException($"Unsafe storage path segment '{name}'.", name);
        }

        return value;
    }

    /// <summary>The prefix every object of the tenant starts with.</summary>
    public static string PrefixFor(Guid tenantId) => tenantId == Guid.Empty ? throw new ArgumentException("Tenant id is required.", nameof(tenantId)) : tenantId.ToString("N") + "/";

    /// <summary>Refuses anything that is not exactly one tenant's prefix.</summary>
    public static string RequireTenantPrefix(string prefix) =>
        prefix.Length == 33 && prefix[32] == '/' && Guid.TryParseExact(prefix[..32], "N", out var id) && id != Guid.Empty
            ? prefix
            : throw new ArgumentException("Only a whole tenant prefix may be deleted.", nameof(prefix));

    /// <summary>True when the key lies inside the tenant's prefix and contains no traversal.</summary>
    public static bool BelongsTo(string key, Guid tenantId) =>
        !string.IsNullOrEmpty(key)
        && key.StartsWith(tenantId.ToString("N") + "/", StringComparison.Ordinal)
        && !key.Split('/', '\\').Any(s => s is ".." or ".")
        && !key.Contains('\\', StringComparison.Ordinal);
}

public interface IBlobBackend
{
    Task PutAsync(string key, Stream content, CancellationToken ct = default);
    Task<Stream?> GetAsync(string key, CancellationToken ct = default);
    Task DeleteAsync(string key, CancellationToken ct = default);

    /// <summary>
    /// Deletes every object whose key starts with the prefix and returns how many were deleted. Only for tenant offboarding: the prefix must be
    /// a whole tenant prefix ("{tenantN}/"), so a wrong argument cannot empty the store.
    /// </summary>
    Task<int> DeletePrefixAsync(string prefix, CancellationToken ct = default);

    /// <summary>The keys of every object whose key starts with the prefix (empty = all), in key order.</summary>
    IAsyncEnumerable<string> ListKeysAsync(string prefix, CancellationToken ct = default);
}

public interface IFileStore
{
    Task<string> PutAsync(Guid? legalEntityId, string kind, Guid id, string fileName, Stream content, CancellationToken ct = default);
    Task<Stream?> GetAsync(string key, CancellationToken ct = default);
    Task DeleteAsync(string key, CancellationToken ct = default);
}

/// <summary>Wraps a storage backend and refuses any key outside the current tenant's prefix.</summary>
public sealed class TenantScopedFileStore(IBlobBackend backend, ITenantContext tenant) : IFileStore
{
    public async Task<string> PutAsync(Guid? legalEntityId, string kind, Guid id, string fileName, Stream content,
        CancellationToken ct = default)
    {
        var key = TenantStoragePath.Build(tenant.RequireTenantId(), legalEntityId, kind, id, fileName);
        await backend.PutAsync(key, content, ct);
        return key;
    }

    public Task<Stream?> GetAsync(string key, CancellationToken ct = default) =>
        backend.GetAsync(Guard(key), ct);

    public Task DeleteAsync(string key, CancellationToken ct = default) =>
        backend.DeleteAsync(Guard(key), ct);

    private string Guard(string key) =>
        TenantStoragePath.BelongsTo(key, tenant.RequireTenantId())
            ? key
            : throw new TenantViolationException("Storage key is outside the current tenant.");
}

public sealed class InMemoryBlobBackend : IBlobBackend
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte[]> _blobs = new();

    public async Task PutAsync(string key, Stream content, CancellationToken ct = default)
    {
        using var ms = new MemoryStream();
        await content.CopyToAsync(ms, ct);
        if (!_blobs.TryAdd(key, ms.ToArray()))
        {
            throw new InvalidOperationException("Storage key already exists; objects are write-once.");
        }
    }

    public Task<Stream?> GetAsync(string key, CancellationToken ct = default) =>
        Task.FromResult<Stream?>(_blobs.TryGetValue(key, out var b) ? new MemoryStream(b) : null);

    public Task DeleteAsync(string key, CancellationToken ct = default)
    {
        _blobs.TryRemove(key, out _);
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<string> ListKeysAsync(string prefix, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var key in _blobs.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToList())
        {
            ct.ThrowIfCancellationRequested();
            yield return key;
        }

        await Task.CompletedTask;
    }

    public Task<int> DeletePrefixAsync(string prefix, CancellationToken ct = default)
    {
        TenantStoragePath.RequireTenantPrefix(prefix);
        var count = 0;
        foreach (var key in _blobs.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
        {
            if (_blobs.TryRemove(key, out _)) count++;
        }

        return Task.FromResult(count);
    }
}
