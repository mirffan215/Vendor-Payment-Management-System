using Microsoft.EntityFrameworkCore;
using Vpms.Audit.Application;
using Vpms.Audit.Domain;
using Vpms.BuildingBlocks.Storage;
using Vpms.BuildingBlocks.Tenancy;

namespace Vpms.Audit.Infrastructure;

public sealed class AuditAnchorService(AuditDbContext db, IAuditAnchorSink sink, TenantContext tenant, TimeProvider clock)
    : IAuditAnchorService
{
    /// <returns>False when nothing new has been written since the last anchor.</returns>
    public async Task<bool> AnchorAsync(Guid tenantId, CancellationToken ct = default)
    {
        using var _ = tenant.BeginPlatformScope();
        var head = await db.ChainHeads.AsNoTracking().FirstOrDefaultAsync(h => h.TenantId == tenantId, ct);
        if (head is null || head.Sequence == 0)
        {
            return false;
        }

        if (await db.Anchors.AnyAsync(a => a.TenantId == tenantId && a.Sequence >= head.Sequence, ct))
        {
            return false;
        }

        var reference = await sink.PublishAsync(tenantId, head.Sequence, head.Hash, ct);
        db.Anchors.Add(new AuditAnchor
        {
            Id = Guid.NewGuid(), TenantId = tenantId, Sequence = head.Sequence, Hash = head.Hash,
            AnchoredAt = clock.GetUtcNow(), ExternalRef = reference,
        });
        await db.SaveChangesAsync(ct);
        return true;
    }
}

/// <summary>What is written for each anchor or restore marker, however it is stored.</summary>
internal sealed record AnchorRecord(string Kind, Guid TenantId, long Sequence, string Hash, DateTimeOffset PublishedAt, string? Reason);

internal static class AnchorFormat
{
    private static readonly System.Text.Json.JsonSerializerOptions Json = new(System.Text.Json.JsonSerializerDefaults.Web);

    /// <summary>Names sort by publication time, so listing them in name order is listing them in the order they were written.</summary>
    public static string Name(AnchorRecord r) => $"{r.PublishedAt.UtcTicks:D19}-{r.Kind}-{r.Sequence:D12}-{(r.Hash.Length >= 16 ? r.Hash[..16] : r.Hash)}.json";

    public static byte[] Serialize(AnchorRecord r) => System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(r, Json);

    public static ExternalAnchor? Parse(byte[] bytes)
    {
        var r = System.Text.Json.JsonSerializer.Deserialize<AnchorRecord>(bytes, Json);
        return r is null ? null : new ExternalAnchor(r.Kind, r.Sequence, r.Hash, r.PublishedAt, r.Reason);
    }
}

/// <summary>Writes anchors as write-once files. For development and tests; production uses <see cref="BlobAuditAnchorSink"/> in a container with an immutability policy.</summary>
public sealed class FileAuditAnchorSink(string directory) : IAuditAnchorSink
{
    public Task<string> PublishAsync(Guid tenantId, long sequence, string hash, CancellationToken ct = default) =>
        WriteAsync(new AnchorRecord(ExternalAnchorKind.Anchor, tenantId, sequence, hash, DateTimeOffset.UtcNow, null), ct);

    public Task<string> PublishRestoreMarkerAsync(Guid tenantId, long lastSequence, string reason, CancellationToken ct = default) =>
        WriteAsync(new AnchorRecord(ExternalAnchorKind.Restore, tenantId, lastSequence, "-", DateTimeOffset.UtcNow, reason), ct);

    public async Task<IReadOnlyList<ExternalAnchor>> ListAsync(Guid tenantId, CancellationToken ct = default)
    {
        var dir = Path.Combine(directory, tenantId.ToString("N"));
        if (!Directory.Exists(dir)) return [];
        var result = new List<ExternalAnchor>();
        foreach (var file in Directory.EnumerateFiles(dir, "*.json").Order(StringComparer.Ordinal))
        {
            if (AnchorFormat.Parse(await File.ReadAllBytesAsync(file, ct)) is { } a) result.Add(a);
        }

        return result;
    }

    private async Task<string> WriteAsync(AnchorRecord record, CancellationToken ct)
    {
        var dir = Path.Combine(directory, record.TenantId.ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, AnchorFormat.Name(record));
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        await stream.WriteAsync(AnchorFormat.Serialize(record), ct);
        return "file:" + path;
    }
}

/// <summary>
/// Writes anchors to a blob container other than the one holding the files. The container is given a time-based immutability policy by the
/// deployment, so even an administrator cannot change or delete an anchor during its retention period; objects are write-once in code as well.
/// </summary>
public sealed class BlobAuditAnchorSink(IBlobBackend anchors) : IAuditAnchorSink
{
    public Task<string> PublishAsync(Guid tenantId, long sequence, string hash, CancellationToken ct = default) =>
        WriteAsync(new AnchorRecord(ExternalAnchorKind.Anchor, tenantId, sequence, hash, DateTimeOffset.UtcNow, null), ct);

    public Task<string> PublishRestoreMarkerAsync(Guid tenantId, long lastSequence, string reason, CancellationToken ct = default) =>
        WriteAsync(new AnchorRecord(ExternalAnchorKind.Restore, tenantId, lastSequence, "-", DateTimeOffset.UtcNow, reason), ct);

    public async Task<IReadOnlyList<ExternalAnchor>> ListAsync(Guid tenantId, CancellationToken ct = default)
    {
        var result = new List<ExternalAnchor>();
        await foreach (var key in anchors.ListKeysAsync(TenantStoragePath.PrefixFor(tenantId), ct))
        {
            await using var stream = await anchors.GetAsync(key, ct);
            if (stream is null) continue;
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, ct);
            if (AnchorFormat.Parse(buffer.ToArray()) is { } a) result.Add(a);
        }

        return result;
    }

    private async Task<string> WriteAsync(AnchorRecord record, CancellationToken ct)
    {
        var key = TenantStoragePath.PrefixFor(record.TenantId) + AnchorFormat.Name(record);
        await anchors.PutAsync(key, new MemoryStream(AnchorFormat.Serialize(record)), ct);
        return "blob:" + key;
    }
}
