using Microsoft.EntityFrameworkCore;
using Vpms.Audit.Application;
using Vpms.Audit.Domain;
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

/// <summary>Writes anchors as write-once files. A stand-in for blob immutability / object lock (Phase 8).</summary>
public sealed class FileAuditAnchorSink(string directory) : IAuditAnchorSink
{
    public async Task<string> PublishAsync(Guid tenantId, long sequence, string hash, CancellationToken ct = default)
    {
        var dir = Path.Combine(directory, tenantId.ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{sequence:D12}.json");
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        await System.Text.Json.JsonSerializer.SerializeAsync(stream, new { tenantId, sequence, hash, anchoredAt = DateTimeOffset.UtcNow }, cancellationToken: ct);
        return "file:" + path;
    }
}
