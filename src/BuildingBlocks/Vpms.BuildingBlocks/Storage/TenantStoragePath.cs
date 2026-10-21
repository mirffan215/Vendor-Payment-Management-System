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
        _blobs[key] = ms.ToArray();
    }

    public Task<Stream?> GetAsync(string key, CancellationToken ct = default) =>
        Task.FromResult<Stream?>(_blobs.TryGetValue(key, out var b) ? new MemoryStream(b) : null);

    public Task DeleteAsync(string key, CancellationToken ct = default)
    {
        _blobs.TryRemove(key, out _);
        return Task.CompletedTask;
    }
}
